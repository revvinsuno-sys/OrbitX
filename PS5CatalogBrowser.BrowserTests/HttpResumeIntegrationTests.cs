using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class HttpResumeIntegrationTests
{
    public static async Task RunAsync()
    {
        using var server = new RangeFixtureServer();
        var root = Path.Combine(Path.GetTempPath(), "PS5HttpResumeUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        MainViewModel? model = null;
        try
        {
            SettingsStore Store(string name)
            {
                var store = new SettingsStore(Path.Combine(root, name, "settings"));
                store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, name, "downloads") }); return store;
            }
            CatalogArtifact Artifact(string mode) => new() { Id = mode, FileName = "fixture.pkg", Url = server.Url(mode), Size = server.Bytes.Length, Sha256 = server.Digest };
            var entry = new CatalogEntry { SourceId = "synthetic", Id = "entry", Title = "Synthetic HTTP fixture" };
            var artifact = Artifact("good"); var store = Store("window");
            window = new MainWindow(store); window.Show(); model = (MainViewModel)window.DataContext;
            SelectDownloads(window);
            var downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { CanPause: true, DownloadedBytes: >= 64 * 1024 });
            var job = model.SelectedJob!; var originalId = job.Id; var selection = job.SelectionIdentity;
            FindButton(window, "Pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await downloading; await WaitAsync(() => job.State == "Paused" && !model.HasDirectTransfers);
            Check(File.Exists(job.StagingPath) && job.CanResume && job.ExpectedFileName == artifact.FileName && store.Load().Downloads.Single().HttpCheckpoint!.Cursor == job.DownloadedBytes, "WPF Pause joins writer and saves cursor/original filename");
            await Reject(() => model.DownloadAsync(entry, artifact));
            Check(model.Downloads.Count == 1, "paused job retains A2 artefact reservation");
            await model.ClearAllDownloadsAsync(); Check(model.Downloads.Count == 1 && File.Exists(job.StagingPath), "A3 history clear keeps paused partial");
            window.Close(); window = null; model = null;
            var requests = server.Requests.Count;
            window = new MainWindow(store); window.Show(); model = (MainViewModel)window.DataContext;
            SelectDownloads(window);
            job = model.Downloads.Single(); model.SelectedJob = job;
            Check(job.State == "Paused" && job.CanResume && server.Requests.Count == requests, "WPF reopen performs no download request");
            await Reject(() => model.DownloadAsync(entry, artifact));
            FindButton(window, "Resume").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => job.State == "Downloaded" && !model.HasDirectTransfers);
            Check(job.Id == originalId && job.SelectionIdentity == selection && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes) &&
                job.ActualSha256 == server.Digest && store.Load().Library.Count == 1 && server.Requests.Last().Range.Length > 0,
                "WPF resume after reopen sends range, matches SHA/bytes and registers once");
            await Reject(() => model.RestartAsync(job));
            await Reject(model.DiscardSelectedDownloadAsync);
            Check(File.Exists(job.Destination), "completed file protected from restart and discard");
            window.Close(); window = null; model = null;

            // Closing an active HTTP writer runs the same final checkpoint and awaits termination.
            store = Store("close-active"); window = new MainWindow(store); window.Show(); model = (MainViewModel)window.DataContext;
            downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { DownloadedBytes: >= 64 * 1024 });
            job = model.SelectedJob!; window.Close(); await downloading;
            await WaitAsync(() => !window.IsVisible); window = null; model = null;
            Check(store.Load().Downloads.Single().State == "Paused", "closing active application retains final durable partial");

            store = Store("competing"); model = new MainViewModel(store);
            downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { CanPause: true, DownloadedBytes: >= 64 * 1024 });
            await model.PauseSelectedDownloadAsync(); await downloading; job = model.SelectedJob!;
            var resuming = model.ResumeSelectedDownloadAsync();
            await Reject(model.ResumeSelectedDownloadAsync);
            await WaitAsync(() => job.CanPause);
            await model.PauseSelectedDownloadAsync(); await resuming;
            var sibling = Path.Combine(Path.GetDirectoryName(job.StagingPath)!, "user-file.txt"); File.WriteAllText(sibling, "keep me");
            // Restart refuses a nonempty owner directory after deleting its exact payload, preserving the sibling.
            await Reject(() => model.RestartAsync(job)); Check(File.ReadAllText(sibling) == "keep me", "restart preserves non-owned sibling");
            await model.DiscardSelectedDownloadAsync();
            Check(!model.Downloads.Contains(job) && !File.Exists(job.StagingPath) && File.ReadAllText(sibling) == "keep me", "discard preserves sibling and releases retained owner");
            model.Dispose(); model = null;

            store = Store("restart"); model = new MainViewModel(store);
            downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { CanPause: true, DownloadedBytes: >= 64 * 1024 });
            await model.PauseSelectedDownloadAsync(); await downloading; job = model.SelectedJob!; originalId = job.Id;
            await model.RestartAsync(job);
            Check(job.State == "Downloaded" && job.Id == originalId && model.Downloads.Count == 1 && model.Library.Count == 1 &&
                server.Requests.Last().Range.Length == 0, "explicit restart reuses original job from zero");
            model.Dispose(); model = null;

            store = Store("restart-race"); model = new MainViewModel(store);
            downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { CanPause: true, DownloadedBytes: >= 64 * 1024 });
            await model.PauseSelectedDownloadAsync(); await downloading; job = model.SelectedJob!;
            using (var release = new ManualResetEventSlim())
            {
                var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                store.BeforeSave = candidate => { if (candidate.Downloads.Single().CleanupPending) { saving.TrySetResult(); release.Wait(); } };
                var restarting = model.RestartAsync(job);
                try
                {
                    await saving.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Reject(model.ResumeSelectedDownloadAsync); await Reject(() => model.RestartAsync(job));
                    await Reject(model.DiscardSelectedDownloadAsync); await Reject(() => model.DownloadAsync(entry, artifact));
                    Check(File.Exists(job.StagingPath), "pending restart intent keeps partial and sole admission");
                }
                finally { release.Set(); }
                await restarting; store.BeforeSave = null;
                Check(job.State == "Downloaded" && model.Library.Count == 1 && model.Downloads.Count == 1, "restart races cannot create another writer or job");
            }
            model.Dispose(); model = null;

            store = Store("cancel-reopen"); model = new MainViewModel(store);
            downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { DownloadedBytes: >= 64 * 1024 }); job = model.SelectedJob!;
            model.CancelSelectedDownload(); await downloading;
            Check(job.State == "Cancelled" && !job.CanResume && File.Exists(job.StagingPath), "explicit cancel settles and retains data without resume action");
            model.Dispose(); model = new MainViewModel(store);
            Check(model.Downloads.Single().State == "Cancelled", "restart preserves explicit cancellation and released admission");
            await model.DownloadAsync(entry, artifact);
            Check(model.Downloads.Count == 2 && model.Library.Count == 1, "cancelled artefact is available for a new explicit admission after restart");
            model.Dispose(); model = null;

            foreach (var mode in new[] { "missing", "ignored" })
            {
                store = Store("nonresume-" + mode); model = new MainViewModel(store);
                var selected = Artifact(mode); downloading = model.DownloadAsync(entry, selected);
                await WaitAsync(() => model.SelectedJob is { DownloadedBytes: >= 64 * 1024 }); job = model.SelectedJob!;
                if (mode == "missing") { Check(!job.CanPause, "missing ETag never offers HTTP pause/resume"); model.CancelSelectedDownload(); }
                else await model.PauseSelectedDownloadAsync();
                await downloading;
                if (mode == "ignored")
                {
                    await model.ResumeSelectedDownloadAsync(); Check(!job.CanResume && job.State == "Needs attention" && File.Exists(job.StagingPath), "HTTP 200 offers explicit restart and keeps partial");
                }
                else await Reject(model.ResumeSelectedDownloadAsync);
                await model.RestartAsync(job);
                Check(job.State == "Downloaded" && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes) && model.Library.Count == 1, "non-resumable workflow restarts safely: " + mode);
                model.Dispose(); model = null;
            }

            store = Store("discard-active"); model = new MainViewModel(store);
            downloading = model.DownloadAsync(entry, artifact);
            await WaitAsync(() => model.SelectedJob is { DownloadedBytes: >= 64 * 1024 }); job = model.SelectedJob!;
            await model.DiscardSelectedDownloadAsync(); await downloading;
            Check(!model.HasDirectTransfers && !model.Downloads.Contains(job) && !File.Exists(job.StagingPath) && model.Library.Count == 0, "discard joins active writer before deleting");
            model.Dispose(); model = null;
            Console.WriteLine("PASS WPF HTTP controls: pause, close/reopen, range resume, SHA/bytes, one registration, admission, competing actions, restart and joined discard; unsafe-range restart");
        }
        finally
        {
            if (model is not null) { await model.StopDirectDownloadsAsync(); if (window is null) model.Dispose(); }
            window?.Close(); Directory.Delete(root, true);
        }
    }
    private static Button FindButton(DependencyObject node, string content)
    {
        if (node is Button button && Equals(button.Content, content)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        { try { return FindButton(VisualTreeHelper.GetChild(node, i), content); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(content);
    }
    private static void SelectDownloads(DependencyObject node)
    {
        if (node is TabItem item && Equals(item.Header, "Downloads")) { item.IsSelected = true; }
        if (node is Expander expander && Equals(expander.Header, "More actions")) expander.IsExpanded = true;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) SelectDownloads(child);
        if (node is Window window) window.UpdateLayout();
    }
    private static async Task WaitAsync(Func<bool> predicate)
    { var until = DateTime.UtcNow.AddSeconds(15); while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("HTTP UI state did not settle"); await Task.Delay(5); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { return; } throw new Exception("Expected safe UI rejection"); }
}
