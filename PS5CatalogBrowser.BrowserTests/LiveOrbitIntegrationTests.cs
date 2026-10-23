using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;
using static LocalOrbitCatalogTests;
using static LiveOrbitCatalogTests;

internal static class LiveOrbitIntegrationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "OrbitLiveUI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CatalogSource? other = null;
        try
        {
            var key = Path.Combine(root, "key.bin"); File.WriteAllBytes(key, Key);
            var local = Path.Combine(root, "local.enc"); File.WriteAllBytes(local, Seal(Feed(generation: 7)));
            var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
            var fixture = new Fixture();
            var clock = new ManualClock();
            using (var model = new MainViewModel(store, new HttpTransfer(new OtherSource()), new HttpTransfer(fixture), clock))
            {
                model.AddSource("Homebrew", "https://fixture.invalid/catalog.json"); other = model.SelectedSource!;
                model.AddLiveOrbitSource("Orbit Store", LiveOrbitCatalog.OfficialUrl, key, true, true);
                var source = model.SelectedSource!;
                await model.RefreshStartupAsync();
                Check(fixture.Calls == 1 && model.Entries.Count == 3 && source.FeedGeneration == 7 && source.Revision == 27 && source.GameCount == 2 && source.ReleaseCount == 3, "startup live and source isolation");
                Check(model.Downloads.Count == 0 && model.Library.Count == 0 && model.Installations.Count == 0 && !Directory.Exists(model.DownloadRoot), "no automatic game activity");
                var game = model.Entries.First(e => e.SourceId == source.Id && e.Platform == "PS5");
                model.SelectedEntry = game;
                model.SelectedMirror = model.MirrorChoices.Single(m => m.Artifact.Id.EndsWith(":b"));
                var artifact = model.SelectedArtifact!;
                var identity = DownloadAdmission.Capture(game, artifact);
                var selection = OrbitReleaseSelection.Capture(game, artifact);
                var job = new DownloadJob { Id = "history", Source = game.SourceName, EntryId = game.Id, SourceId = game.SourceId,
                    ArtifactId = artifact.Id, Url = artifact.Url, FileName = artifact.FileName, ExpectedSize = artifact.Size,
                    ExpectedSha256 = artifact.Sha256, SelectionIdentity = identity, OrbitRelease = selection, State = "Cancelled" };
                model.Settings.Downloads.Add(job); model.Downloads.Add(job);
                var record = new LocalLibraryEntry { Id = "library-history", Title = "Existing library record" };
                model.Settings.Library.Add(record); model.Library.Add(record);
                var frozenJob = JsonSerializer.Serialize(job); var frozenLibrary = JsonSerializer.Serialize(record);
                fixture.Response = _ => Task.FromResult(Ok(Seal(Feed(generation: 7, revision: 28))));
                await model.RefreshAsync(source);
                Check(source.Revision == 28 && model.SelectedArtifact?.Id == artifact.Id && DownloadAdmission.Capture(model.SelectedEntry!, model.SelectedArtifact!) == identity, "refresh retains selection identity");
                await model.RefreshAutomaticAsync(); Check(fixture.Calls == 2, "automatic minimum interval");
                clock.Now += TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1);
                await model.RefreshAutomaticAsync(); Check(fixture.Calls == 2, "no automatic fetch before six hours");
                clock.Now += TimeSpan.FromSeconds(1);
                await model.RefreshAutomaticAsync(); Check(fixture.Calls == 3, "automatic fetch at six hours");
                var before = model.Entries.Where(e => e.SourceId == source.Id).ToArray();
                foreach (var bad in new[] { Seal(Feed(generation: 8)), new byte[] { 1, 2 }, Seal(Feed(generation: 7, revision: 27)), Seal(Feed(generation: 7, revision: 28, remove: true)) })
                {
                    fixture.Response = _ => Task.FromResult(Ok(bad)); await model.RefreshAsync(source);
                    Check(before.SequenceEqual(model.Entries.Where(e => e.SourceId == source.Id)) && source.LastError.Length > 0 && source.Revision == 28, "failed refresh retains entire snapshot");
                }
                fixture.Response = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                await model.RefreshAsync(source); Check(before.SequenceEqual(model.Entries.Where(e => e.SourceId == source.Id)) && model.Entries.Any(e => e.SourceId == other.Id), "HTTP failure isolated");
                var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Response = token => gate.Task.WaitAsync(token); var count = fixture.Calls;
                var first = model.RefreshAsync(source); var second = model.RefreshAsync(source);
                gate.SetResult(Ok(Seal(Feed(generation: 7, revision: 29, remove: true)))); await Task.WhenAll(first, second);
                Check(fixture.Calls == count + 1 && source.Revision == 29 && model.SelectedArtifact is null, "concurrent requests and removed selection");
                Check(JsonSerializer.Serialize(job) == frozenJob && JsonSerializer.Serialize(record) == frozenLibrary, "refresh preserves download history and library");
                var persisted = File.ReadAllText(store.FilePath);
                Check(!persisted.Contains("synthetic-password") && !persisted.Contains(Convert.ToHexString(Key)) && !persisted.Contains("art.invalid"), "no catalogue/key/password persisted");
                Check(!File.Exists(new CatalogCache().FilePath(source)), "no live plaintext cache");
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, false, true);
                count = fixture.Calls; await model.RefreshAsync(source); await model.RefreshStartupAsync(); await model.RefreshAutomaticAsync();
                Check(fixture.Calls == count && model.Entries.All(e => e.SourceId != source.Id), "disabled never fetches");
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, true, false);
                await model.RefreshStartupAsync(); Check(fixture.Calls == count, "startup opt out");
                fixture.Response = _ => Task.FromResult(Ok(Seal(Feed(generation: 7))));
                var lastRefresh = source.LastSuccessfulRefresh;
                await model.RefreshAsync(source, true); Check(model.Entries.All(e => e.SourceId != source.Id) && source.LastSuccessfulRefresh == lastRefresh, "test connection does not publish or replace refresh metadata");
                await model.RefreshAsync(source);
                model.AddLocalOrbitSource("Local fallback", local, key); await model.RefreshAsync(model.SelectedSource!);
                Check(model.BrowseView.Cast<CatalogEntry>().Count() == 3, "identical local and live feed displayed once");
                var fallback = model.SelectedSource!;
                File.WriteAllBytes(local, Seal(Feed(generation: 6, remove: true)));
                await model.RefreshAsync(fallback);
                Check(model.BrowseView.Cast<CatalogEntry>().Count() == 5, "different unlinked catalogues are not merged");
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, true, false, fallback.Id);
                Check(model.BrowseView.Cast<CatalogEntry>().Count() == 3 && store.Load().Sources.Single(s => s.Id == source.Id).LocalFallbackSourceId == fallback.Id, "explicit older fallback hidden and link persisted");
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, false, false, fallback.Id);
                Check(model.BrowseView.Cast<CatalogEntry>().Count() == 3, "local fallback visible when live disabled");
                try { await model.DownloadAllAsync(game); throw new Exception("allowed batch"); } catch (InvalidOperationException) { }
                // Removing a source while its request is in flight must not publish or recreate it.
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, true, true);
                gate = new(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Response = token => gate.Task.WaitAsync(token);
                first = model.RefreshAsync(source);
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, false, true);
                model.UpdateLiveOrbitSource(source, "Orbit Store", source.Url, key, true, true);
                gate.SetResult(Ok(Seal(Feed(generation: 7, revision: 39)))); await first;
                Check(model.Entries.All(e => e.SourceId != source.Id), "disable/re-enable invalidates in-flight results");
                gate = new(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Response = token => gate.Task.WaitAsync(token);
                first = model.RefreshAsync(source); model.RemoveSource(source); gate.SetResult(Ok(Seal(Feed(generation: 7, revision: 40)))); await first;
                Check(model.Entries.All(e => e.SourceId != source.Id), "removed source cannot publish stale result");
            }
            // Settings UI and automatic startup use the same controlled encrypted response.
            store.Save(new() { Sources = [] });
            var uiFixture = new Fixture();
            var window = new MainWindow(store, new HttpTransfer(uiFixture)) { ShowActivated = false };
            try
            {
                var model = (MainViewModel)window.DataContext;
                var type = (ComboBox)window.FindName("SourceTypeBox"); type.SelectedIndex = 2;
                Check(((TextBox)window.FindName("SourceUrlBox")).Text == LiveOrbitCatalog.OfficialUrl && model.Sources.Count == 0, "prefill without automatic source setup");
                model.AddLiveOrbitSource("Orbit Store", LiveOrbitCatalog.OfficialUrl, key, true, true);
                await model.RefreshStartupAsync(); window.Show(); await Task.Delay(150); window.UpdateLayout();
                Check(type.SelectedIndex == 2 && ((TextBox)window.FindName("SourceKeyBox")).Text == key && ((ListBox)window.FindName("BrowseList")).Items.Count == 2, "live settings and Browse controls");
                model.SelectedEntry = model.Entries.First(); Check(model.OrbitBrowseSummary.Contains("feed 7") && model.OrbitBrowseSummary.Contains("3 release options"), "Browse feed summary");
            }
            finally { window.Close(); }
            Console.WriteLine("PASS live Orbit refresh, startup, failure retention, identity, concurrency, fallback, settings and Browse UI");
        }
        finally
        {
            if (other is not null) { var path = new CatalogCache().FilePath(other); if (File.Exists(path)) File.Delete(path); }
            Directory.Delete(root, true);
        }
    }
    private sealed class OtherSource : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri!.AbsoluteUri == "https://fixture.invalid/catalog.json", "other source endpoint unchanged");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"apps\":[{\"id\":\"healthy\",\"name\":\"Homebrew\",\"artifact_url\":\"https://fixture.invalid/game.pkg\"}]}") });
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
