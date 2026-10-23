using System.IO;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class DownloadRetryTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5DownloadRetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var mode in new[] { "good", "ignored" })
            {
                using var server = new RangeFixtureServer(delayMilliseconds: 8);
                var store = Store(Path.Combine(root, mode)); using var model = new MainViewModel(store);
                model.DownloadBandwidthMBps = 0;
                var artifact = new CatalogArtifact { Id = "release", FileName = "fixture.bin", Url = server.Url(mode), Size = server.Bytes.Length, Sha256 = server.Digest };
                var entry = new CatalogEntry { Id = "game", SourceId = "fixture", Title = "Retry fixture", Artifacts = [artifact] };
                var transfer = model.DownloadAsync(entry, artifact);
                await Wait(() => model.SelectedJob is { CanPause: true, DownloadedBytes: > 65536 });
                var job = model.SelectedJob!; var id = job.Id;
                await model.PauseSelectedDownloadAsync(); await transfer;
                job.State = "Failed"; store.Save(model.Settings);
                var before = server.Requests.Count;
                Check(!await model.RetryDownloadAsync(job), "working retry does not request Store");
                var requests = server.Requests.ToArray()[before..];
                Check(requests.Length == (mode == "good" ? 1 : 2) && requests[0].Range.Length > 0 &&
                    (mode == "good" || requests[1].Range.Length == 0), "resume first, then restart from zero only when resume is rejected");
                Check(job.Id == id && model.Downloads.Count == 1 && job.State == "Downloaded" && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes), "retry keeps one original row and verifies exact bytes");
            }
            await NativeAsync(Path.Combine(root, "native"));
            await OrbitAsync(Path.Combine(root, "orbit"));
            Console.WriteLine("PASS Retry: HTTP resume/restart, joined native cancellation and owned-partial replacement, fresh-link Store navigation, same-release history replacement and active-version isolation");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { Console.WriteLine("Retry fixture requires post-process cleanup: " + root); }
            catch (UnauthorizedAccessException) { Console.WriteLine("Retry fixture requires post-process cleanup: " + root); }
        }
    }
    private static async Task NativeAsync(string root)
    {
        var store = Store(root); using var model = new MainViewModel(store);
        var savedArtifact = new CatalogArtifact { Id = "saved-release", FileName = "saved.rar", Size = 64, Url = "https://akirabox.to/original/file", Provider = "AkiraBox" };
        var savedEntry = new CatalogEntry { Id = "saved-game", SourceId = "fixture", CatalogFormat = "Orbit", GameId = "original", Title = "Saved release", Artifacts = [savedArtifact] };
        var savedJob = BrowserDownloadFiles.Prepare(savedEntry, DownloadMirror.ForEntry(savedEntry).Single(), model.DownloadRoot, "saved.rar", model.Downloads);
        savedJob.SelectionIdentity = DownloadAdmission.Capture(savedEntry, savedArtifact);
        model.Settings.Downloads.Add(savedJob); store.Save(model.Settings);
        var reloaded = store.Load().Downloads.Single(job => job.Id == savedJob.Id);
        Check(model.SavedBrowserRelease(reloaded) is { } release && release.Mirror.BrowserUrl == savedJob.Url && reloaded.ExpectedFileName == "saved.rar",
            "saved provider URL and original Orbit selection survive reload without current catalogue");
        reloaded.OrbitRelease = reloaded.OrbitRelease! with { Version = "changed" };
        Check(model.SavedBrowserRelease(reloaded) is null, "changed saved selection cannot be replayed as another release");
        BrowserDownloadFiles.DiscardStaging(savedJob); model.Settings.Downloads.Remove(savedJob); store.Save(model.Settings);
        var entry = new CatalogEntry { Id = "game", SourceId = "fixture", Title = "Native retry fixture" };
        var artifact = new CatalogArtifact { Id = "release", Version = "1", Url = "https://fixture.invalid/landing" }; entry.Artifacts.Add(artifact);
        var mirror = DownloadMirror.ForEntry(entry).Single();
        var missing = BrowserDownloadFiles.Prepare(entry, mirror, model.DownloadRoot, "missing.bin", model.Downloads);
        BrowserDownloadFiles.DiscardStaging(missing);
        missing.State = "Cleanup failed"; model.Settings.Downloads.Add(missing); model.Downloads.Add(missing); store.Save(model.Settings);
        Check(await model.RetryDownloadAsync(missing) && !missing.CleanupPending && missing.State == "Failed" &&
            !Directory.Exists(Path.GetDirectoryName(missing.StagingPath)), "Retry reconciles an absent staging folder and requests the exact Store item without recreating partials");
        var successful = new NativeOwnershipTests.ControlledOperation();
        var successfulJob = model.BrowserDownloads.Accept(entry, mirror, "successful.bin", successful);
        successful.Write(successfulJob.StagingPath, 64); successful.Pause(); successfulJob.State = "Interrupted";
        var successfulRetry = model.RetryDownloadAsync(successfulJob);
        await Wait(() => successfulJob.State == "Downloading"); successful.Complete();
        Check(!await successfulRetry && successfulJob.State == "Downloaded" && File.Exists(successfulJob.Destination), "successful native resume keeps original row and finishes verified content");
        var library = model.Settings.Library.Single(item => item.DownloadArtifacts.Any(a => a.JobId == successfulJob.Id));
        library.Extraction = "Extraction failed";
        Check(model.CanRetryDownload(successfulJob) && await model.RetryDownloadAsync(successfulJob) && File.Exists(successfulJob.Destination), "legacy extraction failure exposes Retry and preserves completed archive while requesting Store");
        var native = new NativeOwnershipTests.ControlledOperation();
        var job = model.BrowserDownloads.Accept(entry, mirror, "fixture.bin", native);
        native.Write(job.StagingPath, 64); native.Pause(); job.State = "Interrupted";
        var installation = new InstallationJob { State = InstallationState.Failed, SourcePath = job.Destination, Title = entry.Title };
        model.Installations.Add(installation);
        using var api = new OrbitFrontendApi(model);
        Check(api.InstallationActions(installation).Contains("retryInstallationDownload"), "failed installation exposes Retry for its exact recorded source download");
        Check(model.CanRetryDownload(job), "interrupted native operation exposes Retry");
        var retry = model.RetryDownloadAsync(job);
        await Wait(() => job.State == "Downloading"); native.InterruptTransfer();
        await Wait(() => native.CancelRequests == 1);
        Check(model.BrowserDownloads.IsActive(job) && File.Exists(job.StagingPath) && !retry.IsCompleted, "failed native resume retains bytes and ownership until termination is confirmed");
        native.Terminate();
        Check(await retry && !File.Exists(job.StagingPath) && model.Downloads.Contains(job), "confirmed termination discards only owned partial and requests a fresh Store selection");
        var alternate = new NativeOwnershipTests.ControlledOperation();
        var alternateArtifact = new CatalogArtifact { Id = artifact.Id, Version = "2", Url = artifact.Url };
        var otherEntry = new CatalogEntry { Id = entry.Id, SourceId = entry.SourceId, Title = entry.Title, Artifacts = [alternateArtifact] };
        var active = model.BrowserDownloads.Accept(otherEntry, DownloadMirror.ForEntry(otherEntry).Single(), "version2.bin", alternate);
        alternate.Write(active.StagingPath, 16);
        var replacement = new NativeOwnershipTests.ControlledOperation();
        var newProvider = new CatalogArtifact { Id = "alternate-provider", Version = "1", Url = "https://other.invalid/landing" };
        var replacementEntry = new CatalogEntry { Id = entry.Id, SourceId = entry.SourceId, Title = entry.Title, Artifacts = [newProvider] };
        var fresh = model.BrowserDownloads.Accept(replacementEntry, DownloadMirror.ForEntry(replacementEntry).Single(), "fixture.bin", replacement);
        Check(job.IsCleared && !model.Downloads.Contains(job) && model.Downloads.Contains(fresh) && model.Downloads.Contains(active) && model.BrowserDownloads.IsActive(active), "new admission replaces failed matching release while a different version continues");
        Check(installation.IsCleared && model.Settings.Downloads.Single(record => record.Id == job.Id).IsCleared && File.Exists(successfulJob.Destination), "replacement clears related failed history while preserving completed original files");
        Check(alternate.CancelRequests == 0 && File.Exists(active.StagingPath), "replacement cannot cancel/delete another version");
        foreach (var (record, operation) in new[] { (active, alternate), (fresh, replacement) })
        { var cancel = model.BrowserDownloads.CancelAsync(record); operation.Terminate(); await cancel; }
    }
    private static async Task OrbitAsync(string root)
    {
        using var server = new BrowserFixtures();
        var store = Store(root, orbit: true); var main = new MainWindow(store) { ShowActivated = false };
        var model = (MainViewModel)main.DataContext;
        try
        {
            main.Show();
            var surface = (ContentControl)main.FindName("OrbitSurface");
            await Wait(() => surface.Content is OrbitFrontendHost);
            var host = (OrbitFrontendHost)surface.Content; await host.InitializeAsync(model);
            model.Sources.Add(new() { Id = "fixture", Name = "Retry fixture" });
            var artifact = new CatalogArtifact { Id = "release", Version = "1", FileName = "fixture.bin", Url = server.Url("/expired") };
            var entry = new CatalogEntry { Id = "game", GameId = "original-game", Platform = "PS5", SourceId = "fixture", Title = "Expired link retry fixture", Artifacts = [artifact] }; model.Entries.Add(entry);
            await model.DownloadAsync(entry, artifact); var job = model.SelectedJob!;
            entry.CatalogFormat = "Orbit";
            job.OrbitRelease = OrbitReleaseSelection.Capture(entry, artifact);
            store.Save(model.Settings);
            var saved = store.Load().Downloads.Single(record => record.Id == job.Id);
            Check(saved.Title == entry.Title && saved.OrbitRelease?.GameId == entry.GameId && saved.OrbitRelease.Platform == "PS5", "download persists original game identity, platform and search title");
            Check(job.State == "Failed" && model.CanRetryDownload(job), "expired URL exposes Retry");
            host.NavigatePage("Downloads");
            var selector = $"[data-windows-id='{job.Id}'] [data-windows-action=retryDownload]";
            await WaitScript(host, $"!!document.querySelector(\"{selector}\")");
            await host.Browser.ExecuteScriptAsync($"document.querySelector(\"{selector}\").click()");
            await WaitScript(host, "!!document.querySelector('.screen-details') && document.body.innerText.includes('Expired link retry fixture') && !document.querySelector('[aria-label=\"Downloads\"].windows-management')");
            Check(model.Interface == "Orbit Store" && surface.IsVisible && model.Downloads.Count == 1, "failed URL retry opens exact game page in Orbit without duplicating history");
            using var api = new OrbitFrontendApi(model);
            Check(api.StoreGameId(job) is not null, "uppercase saved platform resolves lowercase storefront platform");
            model.Entries.Remove(entry);
            model.Sources.Add(new() { Id = "refreshed-fixture", Name = "Re-added source" });
            entry.SourceId = "refreshed-fixture"; entry.Id = "refreshed-game"; model.Entries.Add(entry);
            Check(api.StoreGameId(job) is not null, "original Orbit game ID resolves a re-added source's new local IDs");
            job.OrbitRelease = job.OrbitRelease! with { Platform = "PS4" };
            Check(api.StoreGameId(job) is null, "same game on another platform cannot become an exact match");
            model.Entries.Remove(entry);
            job.Title = "3D MiniGolf \"Search\" & more"; job.State = "Failed";
            host.NavigatePage("Downloads");
            await WaitScript(host, $"!!document.querySelector(\"{selector}\")");
            await host.Browser.ExecuteScriptAsync($"document.querySelector(\"{selector}\").click()");
            await WaitScript(host, "!document.querySelector('.screen-details') && document.querySelector('input[type=search]')?.value === " + System.Text.Json.JsonSerializer.Serialize(job.Title));
            Check(model.Interface == "Orbit Store" && surface.IsVisible, "missing catalogue item retries into a title keyword search inside Orbit");
        }
        finally { main.Close(); }
    }
    private static SettingsStore Store(string root, bool orbit = false)
    {
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [], Interface = orbit ? "Orbit Store" : "Classic Windows", DownloadRoot = Path.Combine(root, "downloads") }); return store;
    }
    private static async Task WaitScript(OrbitFrontendHost host, string script)
    { var until = DateTime.UtcNow.AddSeconds(30); while (await host.Browser.ExecuteScriptAsync(script) != "true") { if (DateTime.UtcNow > until) throw new TimeoutException(script); await Task.Delay(30); } }
    private static async Task Wait(Func<bool> condition)
    { var until = DateTime.UtcNow.AddSeconds(20); while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Retry fixture did not settle"); await Task.Delay(10); } }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
