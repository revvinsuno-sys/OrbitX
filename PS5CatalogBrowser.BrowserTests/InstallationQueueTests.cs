using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class InstallationQueueTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5InstallQueue-" + Guid.NewGuid().ToString("N"));
        MainWindow? main = null;
        var held = new List<TaskCompletionSource<InstallationChoice?>>();
        var running = new List<Task>();
        try
        {
            var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
            var paths = new[] { "first.pkg", "second.pkg", "broken.pkg", "cancelled.pkg", "last.pkg" }.Select(name => Path.Combine(downloads, name)).ToArray();
            foreach (var path in paths) PackageArchiveTests.BuildPackage(path, ps5: true);
            File.WriteAllBytes(paths[2], new byte[4096]);
            using (var file = File.OpenWrite(paths[2])) file.Write(new byte[] { 0x7f, 0x46, 0x49, 0x48 });
            var hashes = paths.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Interface = "Orbit Store", Sources = [], DownloadRoot = downloads,
                InstalledGamesRoot = Path.Combine(root, "installed"), RetainOriginalImage = true });
            main = new(store) { ShowActivated = false }; main.Show();
            var model = (MainViewModel)main.DataContext;
            var surface = (ContentControl)main.FindName("OrbitSurface");
            await Wait(() => Task.FromResult(surface.Content is OrbitFrontendHost), "Orbit host");
            var host = (OrbitFrontendHost)surface.Content;
            await host.InitializeAsync(model);
            await model.RefreshDownloadsOnOpenAsync();
            host.NavigatePage("Downloads");
            var jobs = paths.Select(path => model.Downloads.Single(job => ImageFileSystem.SamePath(job.Destination, path))).ToArray();
            var started = new List<string>();
            model.PropertyChanged += (_, _) =>
            {
                if (model.ActiveInstallation is { } active && started.LastOrDefault() != active.SourcePath) started.Add(active.SourcePath);
            };
            var firstReady = new TaskCompletionSource(); var releaseFirst = Hold();
            var first = Track(model.InstallImageAsync(paths[0], confirmation => { firstReady.TrySetResult(); return releaseFirst.Task; }));
            await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Wait(async () => await Text(jobs[1].Id, "[data-windows-action=installDownload]") == "Queue Install", "Queue Install is available during another installation");
            await Click(jobs[1].Id, "installDownload");
            await Wait(async () => model.IsQueuedForInstallation(paths[1]) && await Text(jobs[1].Id, "[data-windows-action=installDownload]") == "Cancel Install" &&
                (await Text(jobs[1].Id, "[role=status]")).Contains("Queued for installation"), "queued button and status badge");
            Check(model.ActiveInstallation?.SourcePath == paths[0] && !model.Installations.Any(job => job.SourcePath == paths[1]), "queued items do not start early");
            model.SelectedJob = jobs[1];
            await Wait(() => Task.FromResult(((Button)main.FindName("DownloadActionButton")).Content?.ToString() == "Cancel Install" &&
                model.SelectedDownloadInstallationStatus == "Queued for installation"), "Classic queue action and status match Orbit");
            try { _ = model.InstallImageAsync(paths[1]); throw new Exception("duplicate queued installation accepted"); }
            catch (InvalidOperationException) { }
            var broken = Track(model.InstallImageAsync(paths[2]));
            await Click(jobs[3].Id, "installDownload");
            await Wait(() => Task.FromResult(model.IsQueuedForInstallation(paths[3])), "third queued item");
            var last = Track(model.InstallImageAsync(paths[4]));
            await Capture(host);
            await Click(jobs[3].Id, "installDownload");
            await Wait(async () => !model.IsQueuedForInstallation(paths[3]) && await Text(jobs[3].Id, "[data-windows-action=installDownload]") == "Queue Install", "Cancel Install removes only its queued item");
            releaseFirst.SetResult(new("first", false));
            await Task.WhenAll(first, broken, last).WaitAsync(TimeSpan.FromSeconds(45));
            await Wait(() => Task.FromResult(!model.IsInstalling), "queue drained");
            Check(started.SequenceEqual(new[] { paths[0], paths[1], paths[2], paths[4] }), "FIFO execution continues after failure and skips cancelled item");
            Check(model.Installations.Last(job => job.SourcePath == paths[2]).State == InstallationState.Failed &&
                model.Installations.Last(job => job.SourcePath == paths[4]).Registered, "failed predecessor does not block successful successor");
            Check(!model.Installations.Any(job => job.SourcePath == paths[3]), "cancelled queued item created no installation or staging");

            // Queued retries are cancellable, and cancelling the active attempt starts the next item.
            var nextReady = new TaskCompletionSource(); var releaseNext = Hold();
            var active = Track(model.InstallImageAsync(paths[0], _ => { nextReady.TrySetResult(); return releaseNext.Task; }));
            await nextReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var failed = model.Installations.Last(job => job.SourcePath == paths[2]);
            var retry = Track(model.RetryInstallationAsync(failed));
            using var api = new OrbitFrontendApi(model);
            Check(api.InstallationActions(failed).Contains("cancelQueuedInstallation") && api.Allows("cancelQueuedInstallation", failed.Id), "queued failure exposes authorized cancellation");
            await model.RetryInstallationAsync(failed); await retry;
            var afterCancel = Track(model.InstallImageAsync(paths[4]));
            model.CancelInstallation(); releaseNext.SetResult(null);
            await Task.WhenAll(active, afterCancel).WaitAsync(TimeSpan.FromSeconds(30));
            Check(model.Installations.Last(job => job.SourcePath == paths[4]).Registered && failed.State == InstallationState.Failed, "active cancellation advances the queue without rerunning the removed retry");

            // Closing/cancel-all must not start pending work after the active cancellation completes.
            var closingReady = new TaskCompletionSource(); var releaseClosing = Hold();
            var closing = Track(model.InstallImageAsync(paths[0], _ => { closingReady.TrySetResult(); return releaseClosing.Task; }));
            await closingReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            int previousSecond = model.Installations.Count(job => job.SourcePath == paths[1]);
            var abandoned = Track(model.InstallImageAsync(paths[1]));
            model.CancelAllInstallations(); releaseClosing.SetResult(null);
            await Task.WhenAll(closing, abandoned).WaitAsync(TimeSpan.FromSeconds(30));
            Check(!model.IsInstalling && !model.IsQueuedForInstallation(paths[1]) && model.Installations.Count(job => job.SourcePath == paths[1]) == previousSecond, "shutdown clears pending work before cancelling the active installation");
            foreach (var path in paths) Check(SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hashes[path]), "source bytes unchanged: " + Path.GetFileName(path));
            Console.WriteLine("PASS serial install queue: real Orbit Queue/Cancel/badge, FIFO, failure/cancellation advance, duplicate rejection, queued retry, shutdown and source preservation");

            TaskCompletionSource<InstallationChoice?> Hold() { var source = new TaskCompletionSource<InstallationChoice?>(); held.Add(source); return source; }
            Task Track(Task task) { running.Add(task); return task; }
            async Task<string> Text(string id, string selector) => JsonSerializer.Deserialize<string>(await host.Browser.ExecuteScriptAsync(
                "document.querySelector('[data-windows-id=\"" + id + "\"]')?.querySelector(" + JsonSerializer.Serialize(selector) + ")?.textContent?.trim() || ''")) ?? "";
            async Task Click(string id, string action)
            {
                string selector = "[data-windows-id='" + id + "'] [data-windows-action='" + action + "']";
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector(" + JsonSerializer.Serialize(selector) + ") && !document.querySelector(" + JsonSerializer.Serialize(selector) + ").disabled") == "true", "button " + action);
                await host.Browser.ExecuteScriptAsync("document.querySelector(" + JsonSerializer.Serialize(selector) + ").click()");
            }
        }
        finally
        {
            if (main?.DataContext is MainViewModel model) model.CancelAllInstallations();
            foreach (var source in held) source.TrySetResult(null);
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(30)); } catch { }
            main?.Close();
            for (var attempt = 0; attempt < 40 && Directory.Exists(root); attempt++)
                try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
            if (Directory.Exists(root)) throw new IOException("Installation queue fixture remains in use: " + root);
        }
    }
    private static async Task Capture(OrbitFrontendHost host)
    {
        if (Environment.GetEnvironmentVariable("PS5_INSTALL_QUEUE_EVIDENCE") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var output = File.Create(path);
        await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
    }
    private static async Task Wait(Func<Task<bool>> predicate, string message)
    {
        for (int i = 0; i < 150; i++) { if (await predicate()) return; await Task.Delay(100); }
        throw new Exception("Timed out: " + message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
