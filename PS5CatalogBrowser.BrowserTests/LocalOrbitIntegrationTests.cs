using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;
using static LocalOrbitCatalogTests;

internal static class LocalOrbitIntegrationTests
{
    public static async Task RunAsync(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        CatalogSource? healthy = null;
        try
        {
            store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
            var file = Path.Combine(root, "catalog.enc"); var key = Path.Combine(root, "key.bin");
            File.WriteAllBytes(file, Seal(Feed())); File.WriteAllBytes(key, Key);
            if (args.Length == 3 && args[0] == "--orbit-smoke") { file = args[1]; key = args[2]; }
            var handler = new Fixtures();
            using (var model = new MainViewModel(store, new HttpTransfer(handler)))
            {
                model.AddLocalOrbitSource("Local Orbit", file, key);
                var source = model.SelectedSource!;
                await model.RefreshAsync(source, true);
                Check(model.Entries.Count == 0 && source.LastSuccessfulRefresh is not null, "test source without populating Browse");
                await model.RefreshAllAsync();
                Check(handler.Calls == 0 && source.LastError == "", "Orbit never calls HTTP");
                if (args.Contains("--orbit-smoke"))
                {
                    Check(model.BrowseView.Cast<object>().Count() == 1601 && model.Entries.Sum(e => DownloadMirror.ForEntry(e).Count) == 4050, "bundled v6 Browse and release choice counts");
                    Console.WriteLine("PASS local bundled Orbit smoke: 1,601 games / 4,050 releases; " + source.Format);
                    return;
                }
                var ps5 = model.Entries.Single(e => e.Platform == "PS5");
                model.SelectedEntry = ps5; model.SelectedMirror = model.MirrorChoices.Last();
                var chosen = model.SelectedArtifact!.Id; var identity = DownloadAdmission.Capture(ps5, model.SelectedArtifact);
                Check(model.MirrorChoices.Count == 2 && model.MirrorChoices.All(m => !m.DirectEnabled && !m.BrowserEnabled && m.Workflow == ReleaseWorkflow.External), "independent release choices with external fallback");
                Check(model.SelectedArtifact.BrowserUrl.Contains("/page/") && model.SelectedArtifact.Compression == "rar", "URL roles and container metadata");
                File.WriteAllBytes(file, Seal(Feed(reverse: true))); await model.RefreshAsync(source);
                Check(model.SelectedArtifact?.Id == chosen && DownloadAdmission.Capture(model.SelectedEntry!, model.SelectedArtifact!) == identity, "stable selected release after refresh");
                model.Search = "PPSA00001"; Check(model.BrowseView.Cast<object>().Count() == 1, "title ID search");
                model.Search = "synthetic orbit"; Check(model.BrowseView.Cast<object>().Count() == 2, "title search");
                model.PlatformFilter = "PS4"; Check(model.BrowseView.Cast<object>().Count() == 1, "PS4 filter");
                model.PlatformFilter = "All"; model.Search = "";
                async Task Blocked(Func<Task> action)
                {
                    try { await action(); throw new Exception("Orbit action was admitted"); }
                    catch (InvalidOperationException) { }
                }
                await Blocked(() => model.DownloadAsync(model.SelectedEntry!, model.SelectedArtifact!));
                await Blocked(() => model.DownloadAllAsync(model.SelectedEntry!));
                try { await model.BrowserDownloads.ImportAsync(model.SelectedEntry!, model.SelectedMirror!, "missing.pkg"); throw new Exception("Wrong release file accepted"); }
                catch (InvalidDataException) { }
                Check(model.Downloads.Count == 0 && model.Installations.Count == 0 && !Directory.Exists(model.DownloadRoot), "no Orbit transfer/install side effects");
                // Refresh failure keeps the same session objects and chosen release; healthy HTTP source still loads.
                File.WriteAllBytes(file, Seal("{bad"u8.ToArray()));
                model.AddSource("Healthy HTTP", "https://fixture.invalid/catalog.json"); healthy = model.SelectedSource;
                await model.RefreshAllAsync();
                Check(model.Entries.Count == 3 && source.LastError.Contains("last valid in-memory") && !source.LastError.Contains("provider.invalid"), "failure isolation and retained data");
                Check(model.SelectedArtifact?.Id == chosen, "failed refresh retains selection");
                model.SourceFilter = "Local Orbit"; Check(model.BrowseView.Cast<object>().Count() == 2, "source filter");
                model.SourceFilter = "Healthy HTTP"; Check(model.BrowseView.Cast<object>().Count() == 1, "HTTP source filter");
                DownloadAdmission.EnsureEnabled(model.Entries.Single(e => e.SourceId == healthy!.Id));
                var serialized = File.ReadAllText(store.FilePath);
                Check(!serialized.Contains("synthetic-password") && !serialized.Contains("provider.invalid") && !serialized.Contains(Convert.ToHexString(Key)), "only configuration persisted");
                Check(!File.Exists(new CatalogCache().FilePath(source)), "no Orbit cache created");
                var copy = Path.Combine(root, "copy.enc"); var keyCopy = Path.Combine(root, "copy.bin");
                File.WriteAllBytes(copy, Seal(Feed())); File.WriteAllBytes(keyCopy, Key);
                model.UpdateLocalOrbitSource(source, "Renamed Orbit", copy, keyCopy, false);
                await model.RefreshAllAsync(); Check(model.Entries.Count == 1, "disabled source absent");
                model.UpdateLocalOrbitSource(source, "Renamed Orbit", copy, keyCopy, true);
                await model.RefreshAsync(source); Check(model.Entries.Count == 3, "edited paths and re-enabled source");
                File.Delete(keyCopy);
            }
            using (var restarted = new MainViewModel(store, new HttpTransfer(new Fixtures())))
            {
                await restarted.RefreshAllAsync();
                var source = restarted.Sources.Single(s => s.SourceType == CatalogSourceType.OrbitLocal);
                Check(source.Name == "Renamed Orbit" && source.LastError.Contains("key file is missing") && restarted.Entries.Count == 1, "restart configuration and unavailable Orbit source");
                restarted.RemoveSource(source);
                Check(File.Exists(source.EncryptedFilePath) && store.Load().Sources.All(s => s.SourceType == CatalogSourceType.HttpJson), "remove configuration only");
            }
            // WPF Settings controls and Browse action bindings on synthetic data, with no configured network sources.
            store.Save(new() { Sources = [] });
            var window = new MainWindow(store) { ShowActivated = false };
            try
            {
                var model = (MainViewModel)window.DataContext;
                File.WriteAllBytes(file, Seal(Feed()));
                model.AddLocalOrbitSource("UI Orbit", file, key); await model.RefreshAllAsync();
                window.Show(); await Task.Delay(150);
                model.SelectedEntry = model.Entries.First(); window.UpdateLayout();
                Check(((ComboBox)window.FindName("SourceTypeBox")).SelectedIndex == 1 && ((TextBox)window.FindName("SourceFileBox")).Text == file, "settings UI reflects local source");
                var browse = (ListBox)window.FindName("BrowseList");
                Check(browse.Items.Count == 2, "Browse renders Orbit games");
                var actions = Descendants<Button>(window).Where(b => b.Content is "Direct download" or "Browser-assisted" or "Download all files (PS5 Library)").ToArray();
                Check(actions.Length >= 3, "Browse action controls rendered");
                foreach (var button in actions)
                    Check(!button.IsEnabled, "Orbit button disabled");
                var fallback = Descendants<Button>(window).Where(b => b.Content is "External browser" or "Import completed file").ToArray();
                Check(fallback.Length >= 2 && fallback.All(b => b.IsEnabled), "unknown release fallback controls enabled");
                var displayed = model.SelectedEntry!;
                displayed.Artifacts[0].Url = "https://archive.org/download/synthetic/fixture.pkg";
                if (displayed.Artifacts.Count == 1) displayed.Artifacts.Add(new() { Id = "ui-browser", Url = "https://vikingfile.com/f/synthetic", FileName = "fixture.pkg", Size = 1 });
                else { displayed.Artifacts[1].Url = "https://vikingfile.com/f/synthetic"; displayed.Artifacts[1].BrowserUrl = ""; }
                model.SelectedEntry = displayed; window.UpdateLayout();
                var routed = Descendants<Button>(window).ToArray();
                Check(routed.Any(b => b.Content is "Direct download" && b.IsEnabled) && routed.Any(b => b.Content is "Browser-assisted" && b.IsEnabled), "selected release action routing bindings");
                Check(routed.Where(b => b.Content is "Download all files (PS5 Library)").All(b => !b.IsEnabled), "Orbit Download All remains disabled with supported releases");
                Check(Descendants<CoverThumbnail>(window).Any(t => t.Url == displayed.CoverUrl && t.Url.Length > 0), "catalogue artwork bindings");
            }
            finally { window.Close(); }
            Console.WriteLine("PASS local Orbit source lifecycle, Browse, release selection, failure isolation, persistence and workflow guards");
        }
        finally
        {
            if (healthy is not null) { var path = new CatalogCache().FilePath(healthy); if (File.Exists(path)) File.Delete(path); }
            Directory.Delete(root, true);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class Fixtures : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.RequestUri!.AbsoluteUri == "https://fixture.invalid/catalog.json", "only HTTP source requested");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"apps\":[{\"id\":\"healthy\",\"name\":\"Healthy game\",\"artifact_url\":\"https://fixture.invalid/game.pkg\"}]}") });
        }
    }
}
