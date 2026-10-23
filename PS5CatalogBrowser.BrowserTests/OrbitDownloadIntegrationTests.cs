using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;
using static LocalOrbitCatalogTests;

internal static class OrbitDownloadIntegrationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitDownload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        BrowserDownloadWindow? window = null;
        try
        {
            foreach (var path in new[] { "/landing?target=/landing", "/expired" })
            {
                using var model = new MainViewModel(Store(root, path.Contains("expired") ? "unavailable-page" : "no-event"));
                var entry = Entry(server); entry.Artifacts[0].BrowserUrl = server.Url(path);
                var confirmations = 0;
                window = new BrowserDownloadWindow(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads,
                    Path.Combine(root, "profile"), _ => { confirmations++; return true; });
                window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Wait(() => Task.FromResult(window.PageStatus.Text.StartsWith("Website")));
                if (path.Contains("target"))
                {
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
                    await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
                    await Wait(() => Task.FromResult(window.Browser.CoreWebView2.Source == server.Url("/landing")));
                }
                Check(window.WorkflowGuidance.Text.Contains("If no file is offered or the page cannot load") &&
                    window.WorkflowGuidance.Text.Contains("import the matching completed file"), "fallback guidance available for no-event and unavailable pages");
                if (window.PageStatus.Text.StartsWith("Website could not load"))
                    Check(window.PageStatus.Text.Contains("external browser"), "navigation failure offers external fallback");
                window.Close(); window = null;
                Check(confirmations == 0 && model.Downloads.Count == 0 && !model.BrowserDownloads.HasActive,
                    "closing a page before native transfer creates no cancellation or download record");
            }
            foreach (var mode in new[] { "good", "html", "size", "sha" })
            {
                var store = Store(root, mode);
                using var model = new MainViewModel(store, new HttpTransfer(new LocalDirectHandler(server, mode == "html" ? "/landing" : "/file")));
                var entry = Entry(server); var artifact = entry.Artifacts[0];
                if (mode == "size") artifact.Size++;
                if (mode == "sha") artifact.Sha256 = new string('0', 64);
                await model.DownloadAsync(entry, artifact);
                var saved = store.Load(); var job = saved.Downloads.Single();
                Check(job.Method == "Direct" && job.Url == artifact.Url && job.SelectionIdentity == DownloadAdmission.Capture(entry, artifact), "direct selected role and fingerprint persisted");
                Check(job.OrbitRelease == OrbitReleaseSelection.Capture(entry, artifact) && job.Mirror == artifact.Provider, "selected metadata persisted before transfer");
                if (mode == "good")
                {
                    Verify(saved, server.Bytes, artifact);
                    var alternative = entry.Artifacts[1]; alternative.Url = artifact.Url;
                    await model.DownloadAsync(entry, alternative);
                    saved = store.Load();
                    Check(saved.Downloads.Count == 2 && saved.Library.Count == 2 && saved.Downloads.Select(j => j.Destination).Distinct().Count() == 2,
                        "same filename alternatives remain independent");
                    Verify(saved, server.Bytes, alternative);
                }
                else Check(job.State is "Failed" or "Integrity failed" && saved.Library.Count == 0 && !File.Exists(job.Destination), "HTTP 200 is insufficient: " + mode);
                try { await model.DownloadAllAsync(entry); throw new Exception("Orbit Download All admitted"); } catch (InvalidOperationException) { }
            }

            foreach (var mode in new[] { "good", "cancel", "recovery", "wrong-name", "size", "sha" })
            {
                var store = Store(root, "browser-" + mode);
                using (var model = new MainViewModel(store))
                {
                    var entry = Entry(server); var artifact = entry.Artifacts[0];
                    artifact.Url = "https://vikingfile.com/f/synthetic";
                    artifact.BrowserUrl = server.Url("/landing?target=" + (mode == "cancel" ? "/slow" : mode == "wrong-name" ? "/unrelated" : "/redirect"));
                    if (mode == "cancel") { artifact.Size = 4 * 1024 * 1024; artifact.Sha256 = ""; }
                    if (mode == "size") artifact.Size++;
                    if (mode == "sha") artifact.Sha256 = new string('0', 64);
                    var mirror = DownloadMirror.ForEntry(entry)[0];
                    // Explicit controlled local browser fixture; no provider page or commercial content is contacted.
                    window = new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads, Path.Combine(root, "profile"), _ => true);
                    window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
                    Check(model.Downloads.Count == 0, "browser navigation does not automatically download");
                    Check(window.WorkflowGuidance.Text.Contains("does not create a download") && window.WorkflowGuidance.Text.Contains("External transfers must be cancelled in that browser"),
                        "page-only and external cancellation guidance shown before native admission");
                    Check(window.Browser.CoreWebView2.Source == artifact.BrowserUrl, "explicit browserUrl navigated instead of url");
                    var failedSave = false;
                    if (mode == "recovery") store.BeforeSave = candidate =>
                    {
                        if (!failedSave && candidate.Downloads.SingleOrDefault()?.Completion?.Phase == "Promoted")
                        { failedSave = true; throw new IOException("Synthetic promotion persistence failure"); }
                    };
                    await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
                    if (mode == "wrong-name")
                    {
                        await Wait(() => Task.FromResult(window.PageStatus.Text.Contains("filename does not match")));
                        Check(model.Downloads.Count == 0, "unrelated native filename rejected before admission");
                    }
                    else
                    {
                        await Wait(() => Task.FromResult(model.Downloads.Count == 1));
                        var job = model.Downloads.Single();
                        if (mode == "cancel")
                        {
                            await Wait(() => Task.FromResult(job.DownloadedBytes > 0));
                            var external = Path.Combine(root, "fixture.pkg"); File.WriteAllBytes(external, server.Bytes);
                            try { await model.BrowserDownloads.ImportAsync(entry, mirror, external); throw new Exception("Repeated admission accepted"); }
                            catch (IOException) { }
                            // Repeated direct intent for the same stable release cannot take the active browser owner.
                            artifact.Url = "https://archive.org/download/synthetic/fixture.pkg";
                            try { await model.DownloadAsync(entry, artifact); throw new Exception("Cross-workflow admission accepted"); }
                            catch (IOException) { }
                            await model.BrowserDownloads.CancelAsync(job);
                            Check(job.State == "Cancelled" && job.Error.Contains("WebView2 transfer cancelled") && job.Error.Contains("may remove partial bytes") &&
                                File.ReadAllText(store.FilePath).Contains(job.Error), "native cancellation guidance persisted without promising partial bytes");
                            if (!File.Exists(job.StagingPath))
                                Check(store.Load().Downloads.Single().Error.Contains("Owned partial files are missing"), "restart reconciliation reports actual missing partial bytes");
                        }
                        await Wait(() => Task.FromResult(!model.BrowserDownloads.HasActive));
                        var saved = store.Load();
                        Check(saved.Downloads.Single().Url == artifact.BrowserUrl && !File.ReadAllText(store.FilePath).Contains("temporary-secret"), "catalogue page only persisted, native capability omitted");
                        if (mode == "good") Verify(saved, server.Bytes, artifact);
                        else if (mode == "recovery") Check(failedSave && job.State == "Needs attention" && saved.Library.Count == 0, "finalisation failure retained for restart");
                        else Check(saved.Library.Count == 0 && !File.Exists(job.Destination), "incomplete or invalid native bytes never registered: " + mode);
                    }
                    window.Close(); window = null;
                    Console.WriteLine("PASS Orbit native fixture: " + mode);
                }
                store.BeforeSave = null;
                if (mode == "recovery")
                {
                    using var recovered = new MainViewModel(store);
                    Verify(store.Load(), server.Bytes, Entry(server).Artifacts[0], compareSnapshot: false);
                    Check(recovered.Downloads.Single().Completion?.Phase == "Completed", "native finalisation recovered without network");
                }
            }

            var repeatedStore = Store(root, "repeated-direct");
            using (var model = new MainViewModel(repeatedStore, new HttpTransfer(new LocalDirectHandler(server, "/slow"))))
            {
                var entry = Entry(server); var artifact = entry.Artifacts[0]; artifact.Size = 4 * 1024 * 1024; artifact.Sha256 = "";
                var first = model.DownloadAsync(entry, artifact);
                await Wait(() => Task.FromResult(model.Downloads.Single().DownloadedBytes > 0));
                try { await model.DownloadAsync(entry, artifact); throw new Exception("Repeated direct click admitted"); }
                catch (IOException) { }
                model.SelectedJob = model.Downloads.Single(); model.CancelSelectedDownload(); await first;
                Check(model.Downloads.Single().Error.Contains("Application-managed HTTP download cancelled"), "HTTP cancellation names the app-managed transfer");
                Check(repeatedStore.Load().Downloads.Count == 1 && repeatedStore.Load().Library.Count == 0, "one active writer for repeated direct clicks");
            }

            var interruptionStore = Store(root, "interruption");
            var operation = new NativeOwnershipTests.ControlledOperation();
            using (var model = new MainViewModel(interruptionStore))
            {
                var entry = Entry(server); var artifact = entry.Artifacts[0];
                var mirror = DownloadMirror.ForEntry(entry)[0];
                var job = model.BrowserDownloads.Accept(entry, mirror, "fixture.pkg", operation);
                operation.Write(job.StagingPath, 64); operation.InterruptTransfer();
                Check(job.State == "Interrupted" && interruptionStore.Load().Downloads.Single().State == "Interrupted" && model.BrowserDownloads.IsActive(job),
                    "interrupted native Orbit transfer retains session owner until definitive termination");
                try { model.BrowserDownloads.Accept(entry, mirror, "fixture.pkg", operation); throw new Exception("Repeated native callback admitted"); }
                catch (IOException) { }
                Check(model.Library.Count == 0 && File.Exists(job.StagingPath) && !File.Exists(job.Destination), "partial Orbit bytes retained without promotion");
                var cancelled = model.BrowserDownloads.CancelAsync(job); operation.Terminate(); await cancelled;
                Check(!model.BrowserDownloads.IsActive(job) && interruptionStore.Load().Library.Count == 0, "explicit native acknowledgement releases interrupted writer");
            }

            var importStore = Store(root, "import");
            using (var model = new MainViewModel(importStore))
            {
                var entry = Entry(server); var artifact = entry.Artifacts[0]; artifact.Url = "https://unknown.invalid/page"; artifact.BrowserUrl = "";
                var mirror = DownloadMirror.ForEntry(entry)[0];
                Check(mirror.Workflow == ReleaseWorkflow.External && !mirror.DirectEnabled && !mirror.BrowserEnabled, "unsupported provider external fallback");
                var external = Path.Combine(root, "fixture.pkg"); File.WriteAllBytes(external, server.Bytes);
                await model.BrowserDownloads.ImportAsync(entry, mirror, external);
                Verify(importStore.Load(), server.Bytes, artifact);
                Check(File.ReadAllBytes(external).SequenceEqual(server.Bytes) && importStore.Load().Downloads.Single().Method == "External import", "external original retained");
            }
            Console.WriteLine("PASS Orbit selected direct/native/import transfers, alternatives, filename/size/SHA/HTML checks, cancellation, interruption, ownership, persisted metadata and finalisation recovery");
        }
        finally
        {
            window?.Close();
            for (var attempt = 0; attempt < 60 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { await Task.Delay(250); }
                catch (UnauthorizedAccessException) { await Task.Delay(250); }
            }
            if (Directory.Exists(root)) Console.WriteLine("WebView2 profile pending process exit cleanup: " + root);
        }
    }

    private static CatalogEntry Entry(BrowserFixtures server)
    {
        var entry = OrbitCatalogParser.Parse(new() { Id = "local", Name = "Synthetic Orbit" }, Feed()).Entries.Single(e => e.Platform == "PS5");
        foreach (var a in entry.Artifacts)
        {
            a.FileName = a.Name = "fixture.pkg"; a.Size = server.Bytes.Length;
            a.Sha256 = Convert.ToHexString(SHA256.HashData(server.Bytes)).ToLowerInvariant();
            a.Url = "https://archive.org/download/synthetic/fixture.pkg"; a.BrowserUrl = "https://archive.org/details/synthetic";
        }
        return entry;
    }
    private static SettingsStore Store(string root, string name)
    {
        var store = new SettingsStore(Path.Combine(root, name, "settings"));
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, name, "downloads") }); return store;
    }
    private static void Verify(AppSettings saved, byte[] bytes, CatalogArtifact artifact, bool compareSnapshot = true)
    {
        var job = saved.Downloads.Last(); var item = saved.Library.Last(); var registered = item.DownloadArtifacts.Single();
        Check(job.State is "Downloaded" or "Imported" && job.Completion?.Phase == "Completed" && job.ArtifactId == artifact.Id,
            "durable selected release completion");
        Check(File.ReadAllBytes(job.Destination).SequenceEqual(bytes) && item.ContentPath == job.Destination && registered.OrbitRelease == job.OrbitRelease,
            "real bytes and library selection match persisted job");
        Check(registered.OrbitRelease?.Platform == "PS5" && registered.OrbitRelease.Region == artifact.Region && registered.OrbitRelease.Version == artifact.Version &&
            registered.SourceId == "local" && item.Integrity.Contains("SHA-256") && !item.Classification.Contains("Playable"), "library metadata and honest integrity/classification");
        if (compareSnapshot) Check(job.OrbitRelease?.Url == artifact.Url && job.OrbitRelease.BrowserUrl == artifact.BrowserUrl && job.OrbitRelease.FileName == artifact.FileName, "both original URL roles preserved");
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Orbit browser fixture did not reach expected state.");
    }
    private sealed class LocalDirectHandler(BrowserFixtures server, string path) : HttpMessageHandler
    {
        private readonly HttpClient local = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri!.AbsoluteUri == "https://archive.org/download/synthetic/fixture.pkg", "only selected direct route requested");
            return local.GetAsync(server.Url(path), HttpCompletionOption.ResponseHeadersRead, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) local.Dispose(); base.Dispose(disposing); }
    }
}
