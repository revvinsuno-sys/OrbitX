using System.Diagnostics;
using System.IO;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class DownloadBandwidthTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5Bandwidth-" + Guid.NewGuid().ToString("N"));
        using var server = new BrowserFixtures();
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
        using var model = new MainViewModel(store);
        var windows = new List<BrowserDownloadWindow>();
        var clock = new Stopwatch();
        try
        {
            Check(model.DownloadBandwidthMBps == 100, "new and existing settings default to 100 MB/s");
            model.DownloadBandwidthMBps = .05;
            Check(store.Load().DownloadBandwidthMBps == .05, "bandwidth setting persists");
            BrowserDownloadWindow OpenProvider(string id)
            {
                var artifact = new CatalogArtifact { Id = id, Url = server.Url("/landing?target=/file"), FileName = "fixture.pkg", Size = server.Bytes.Length };
                var entry = new CatalogEntry { Id = id, Title = "Synthetic " + id, Artifacts = [artifact] };
                var window = new BrowserDownloadWindow(entry, DownloadMirror.ForEntry(entry).Single(), model.BrowserDownloads, Path.Combine(root, "profile"), _ => true);
                windows.Add(window); window.Show(); return window;
            }
            var first = OpenProvider("first");
            await first.Ready.Task;
            await Wait(async () => await first.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            using (var cancelled = new CancellationTokenSource(100))
            {
                try { await model.Bandwidth.BeginDirectAsync(cancelled.Token); throw new Exception("Queued transfer did not cancel"); }
                catch (OperationCanceledException) { Check(model.Bandwidth.Share == 50000, "cancelling a queued transfer preserves the active provider slot"); }
            }
            clock.Start();
            await first.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await Wait(() => Task.FromResult(model.BrowserDownloads.HasActive));
            model.DownloadBandwidthMBps = 0;
            Check(model.Bandwidth.Share == 50000, "active native rate is frozen until its transfer ends");
            model.DownloadBandwidthMBps = .05;
            var second = OpenProvider("second");
            var directEntry = new CatalogEntry { Id = "direct", Title = "Synthetic direct" };
            var direct = model.DownloadAsync(directEntry, new() { Id = "direct", Name = "direct.pkg", Url = server.Url("/file"), Size = server.Bytes.Length });
            await Task.Delay(200);
            Check(!second.Ready.Task.IsCompleted && !direct.IsCompleted, "provider navigation and HTTP transfer queue behind the active native download");
            await second.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Wait(async () => await second.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            await second.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await direct;
            await Wait(() => Task.FromResult(model.Downloads.Count == 3 && !model.BrowserDownloads.HasActive));
            var elapsed = clock.Elapsed;
            var expected = server.Bytes.Length * 3 / 50000d;
            Check(elapsed.TotalSeconds >= expected - .8, "native and HTTP downloads share one aggregate limit: " + elapsed);
            Check(model.Downloads.All(job => job.State == "Downloaded" && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes)), "throttled native/direct bytes are verified without changed ownership");
            Check(windows.All(window => !window.IsVisible), "accepted provider windows hide completely while downloads continue");
            model.DownloadBandwidthMBps = 0;
            var unlimited = Stopwatch.StartNew();
            await model.DownloadAsync(new() { Id = "unlimited", Title = "Unlimited fixture" }, new() { Id = "unlimited", Name = "unlimited.pkg", Url = server.Url("/file") });
            Check(unlimited.Elapsed.TotalSeconds < 3, "zero disables throttling");
            try { model.DownloadBandwidthMBps = double.NaN; throw new Exception("Invalid rate accepted"); }
            catch (ArgumentException) { Check(model.DownloadBandwidthMBps == 0 && store.Load().DownloadBandwidthMBps == 0, "invalid limit cannot corrupt saved settings"); }
            Console.WriteLine($"PASS shared native/HTTP bandwidth: {server.Bytes.Length * 3} bytes in {elapsed.TotalSeconds:F2}s at 50000 B/s, default/persistence/unlimited/validation and minimisation");
        }
        finally
        {
            foreach (var window in windows) window.Close();
            for (var i = 0; i < 12 && Directory.Exists(root); i++)
                try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(250); } catch (UnauthorizedAccessException) { await Task.Delay(250); }
            if (Directory.Exists(root)) Console.WriteLine("Bandwidth fixture profile requires post-process cleanup: " + root);
        }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Bandwidth fixture timed out."); await Task.Delay(50); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
