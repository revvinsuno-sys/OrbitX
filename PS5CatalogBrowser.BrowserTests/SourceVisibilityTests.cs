using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class SourceVisibilityTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5SourceVisibility-" + Guid.NewGuid().ToString("N"));
        var enabled = new CatalogSource { Name = "Enabled", Url = "https://fixture.invalid/catalogue" };
        var disabled = new CatalogSource { Name = "Disabled", Url = "https://fixture.invalid/disabled", Enabled = false };
        var local = new CatalogSource { Name = "Disabled local", SourceType = CatalogSourceType.OrbitLocal, Enabled = false };
        var live = new CatalogSource { Name = "Disabled live", SourceType = CatalogSourceType.OrbitLive, Enabled = false };
        var cache = new CatalogCache();
        var store = new SettingsStore(root);
        store.Save(new() { Sources = [enabled, disabled, local, live], DownloadRoot = Path.Combine(root, "downloads") });
        var handler = new Fixture();
        try
        {
            using var model = new MainViewModel(store, new HttpTransfer(handler));
            enabled = model.Sources.Single(s => s.Name == "Enabled");
            disabled = model.Sources.Single(s => s.Name == "Disabled");
            Check(model.SourceChoices.SequenceEqual(new[] { "All", "Enabled" }), "startup source choices exclude every disabled source type");
            foreach (var source in model.Sources)
                model.Entries.Add(new() { Id = source.Id + ":fixture", SourceId = source.Id, SourceName = source.Name, Title = source.Name });
            Check(model.BrowseView.Cast<CatalogEntry>().Count() == 1, "disabled source items stay hidden even if present in memory");
            await model.RefreshAsync(disabled);
            Check(handler.Requests == 0, "disabled source cannot refresh");

            model.SourceFilter = enabled.Name;
            model.SelectedEntry = model.Entries.Single(e => e.SourceId == enabled.Id);
            model.UpdateSource(enabled, enabled.Name, enabled.Url, false);
            Check(model.SourceChoices.SequenceEqual(new[] { "All" }) && model.SourceFilter == "All", "disabling removes source choice and resets its active filter");
            Check(model.SelectedEntry is null && model.BrowseView.IsEmpty && !model.Entries.Any(e => e.SourceId == enabled.Id), "disabling clears selected details and source items");

            foreach (var fail in new[] { false, true })
            {
                model.UpdateSource(enabled, enabled.Name, enabled.Url, true);
                await cache.SaveAsync(enabled, Encoding.UTF8.GetBytes(Fixture.Json), CancellationToken.None);
                handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var refresh = model.RefreshAsync(enabled);
                model.SetSourceEnabled(enabled, false);
                model.SetSourceEnabled(enabled, true);
                if (fail) handler.Pending.SetException(new HttpRequestException("Synthetic failure"));
                else handler.Pending.SetResult(Fixture.Response());
                await refresh;
                Check(!model.Entries.Any(e => e.SourceId == enabled.Id), "obsolete refresh cannot restore live or cached items after disable/re-enable");
            }

            handler.Pending = null;
            await model.RefreshAsync(enabled);
            Check(model.BrowseView.Cast<CatalogEntry>().Single().Title == "Fixture game", "re-enabled source loads on a fresh refresh");
            var original = model.BrowseView.Cast<CatalogEntry>().Single();
            var legacyJob = new DownloadJob { SourceId = enabled.Id, EntryId = original.Id };
            var foreignJob = new DownloadJob { SourceId = "different-source", EntryId = original.Id, Title = original.Title };
            var legacyLibrary = new LocalLibraryEntry { CatalogEntryId = original.Id, DownloadArtifacts = [new() { SourceId = enabled.Id }] };
            model.Settings.Downloads.AddRange([legacyJob, foreignJob]);
            model.Settings.Library.Add(legacyLibrary);
            await model.RefreshAsync(enabled);
            Check(legacyJob.CoverUrl == original.CoverUrl && legacyJob.CoverUrl.Length > 0 &&
                legacyLibrary.SourceId == enabled.Id && legacyLibrary.CoverUrl == original.CoverUrl && foreignJob.CoverUrl.Length == 0,
                "legacy artwork is restored only by exact source and catalogue identity");
            var interrupted = false;
            model.Entries.CollectionChanged += (_, _) =>
            {
                if (interrupted || model.Entries.Count(e => e.SourceId == enabled.Id) < 50) return;
                interrupted = true;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => model.UpdateSource(enabled, enabled.Name, enabled.Url, false)));
            };
            handler.JsonBody = "{\"apps\":[" + string.Join(",", Enumerable.Range(0, 150).Select(i => "{\"id\":\"" + i + "\",\"name\":\"Fixture " + i + "\"}")) + "]}";
            await model.RefreshAsync(enabled);
            Check(interrupted && model.BrowseView.IsEmpty && !model.Entries.Any(e => e.SourceId == enabled.Id), "disabling during batched publication prevents remaining items from being restored");
            Check(model.Sources.Count == 4, "disabled sources remain configurable in Settings");
            Console.WriteLine("PASS disabled source choices, filtering, selection, refresh races, cache and re-enable");
        }
        finally
        {
            foreach (var source in new[] { enabled, disabled }) File.Delete(cache.FilePath(source));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        await SourceEditorAsync();
    }

    private static async Task SourceEditorAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5SourceEditor-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
        var keys = new OrbitKeyTests.Fixture();
        var window = new MainWindow(store, keyClient: new OrbitPublishedKeyClient(keys));
        try
        {
            var model = (MainViewModel)window.DataContext;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            model.AddLiveOrbitSource("Fixture", LiveOrbitCatalog.OfficialUrl, "", false, false);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var source = model.SelectedSource!;
            var list = (ListBox)window.FindName("SourceList");
            var enabled = (CheckBox)window.FindName("SourceEnabledBox");
            var name = (TextBox)window.FindName("SourceNameBox");
            var save = (Button)window.FindName("SaveSourceButton");
            Check(list.SelectedItem == source, "source is selected in the editor");
            name.Text = "Unsaved name";
            // A concurrent catalogue refresh notifies the source list while the user edits it.
            await model.RefreshAsync(source);
            model.SetSourceEnabled(source, true);
            Check(list.SelectedItem == source && model.SelectedSource == source && name.Text == "Unsaved name", "list refresh preserves selection and pending editor changes");
            name.Text = source.Name;
            model.SetSourceEnabled(source, false);
            enabled.IsChecked = true;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(source.Enabled && keys.Calls == 0 && !model.IsFetchingOrbitKey, "saving an Enabled-only change does not download a key or refresh content");

            for (var i = 0; i < 2500; i++)
                model.Entries.Add(new() { Id = "entry" + i, SourceId = source.Id, SourceName = source.Name, Title = "Fixture " + i });
            model.SelectedEntry = model.Entries[0];
            var changes = 0;
            model.Entries.CollectionChanged += (_, _) => changes++;
            var viewResets = 0;
            model.BrowseView.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) viewResets++; };
            model.SourceFilter = source.Name;
            foreach (var value in new[] { false, true })
            {
                enabled.IsChecked = value;
                enabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                Check(list.SelectedItem == source && model.SelectedSource == source, "clicking Enabled keeps the selected source");
                var resetsBeforeSave = viewResets;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(source.Enabled == value && store.Load().Sources.Single().Enabled == value, "Save selected persists the flag");
                Check(list.SelectedItem == source && model.SelectedSource == source, "saving preserves the source selection");
                Check(viewResets == resetsBeforeSave, "saving does not rebuild the Browse view");
                model.RefreshBrowseIfNeeded(); // Opening Browse applies pending visibility changes.
                Check(model.BrowseView.Cast<CatalogEntry>().Count() == (value ? 2500 : 0), "Browse applies the saved flag to loaded content");
            }
            Check(changes == 0 && model.Entries.Count == 2500 && model.SelectedEntry is null && keys.Calls == 0,
                "Enabled-only saves retain loaded entries without collection rebuilds or network work");
            Console.WriteLine("PASS source editor selection, unsaved edits and attribute-only save with 2500 retained entries");
        }
        finally
        {
            window.Close();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public const string Json = "{\"apps\":[{\"id\":\"fixture\",\"name\":\"Fixture game\",\"cover\":\"https://fixture.invalid/cover.png\",\"artifact_url\":\"https://fixture.invalid/game.pkg\"}]}";
        public int Requests;
        public string JsonBody = Json;
        public TaskCompletionSource<HttpResponseMessage>? Pending;
        public static HttpResponseMessage Response(string body = Json) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Pending?.Task.WaitAsync(token) ?? Task.FromResult(Response(JsonBody));
        }
    }
}
