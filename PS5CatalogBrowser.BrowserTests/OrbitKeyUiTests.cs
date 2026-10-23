using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;
using static OrbitKeyTests;

internal static class OrbitKeyUiTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5KeyUI-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new SettingsStore(root); store.Save(new() { Sources = [] });
        var fixture = new Fixture();
        var window = new MainWindow(store, keyClient: new OrbitPublishedKeyClient(fixture)) { ShowActivated = false };
        try
        {
            window.Show(); await Task.Delay(100);
            var model = (MainViewModel)window.DataContext;
            var dropdown = (ComboBox)window.FindName("SourceManagedKeyBox");
            ((ComboBox)window.FindName("SourceTypeBox")).SelectedIndex = 2;
            Check(model.ManagedOrbitKeys.Count == 0 && fixture.Calls == 0, "opening settings does not fetch keys");
            ((Button)window.FindName("AddSourceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => model.Sources.Count == 1 && ((StackPanel)window.FindName("SourceEditorPanel")).IsEnabled);
            var source = model.Sources.Single(); var originalKeyPath = source.KeyFilePath;
            Check(fixture.AssetCalls == 2 && dropdown.Items.Count == 1 && dropdown.SelectedValue as string == originalKeyPath &&
                Path.GetDirectoryName(originalKeyPath) == Path.Combine(root, "keys") && File.ReadAllBytes(originalKeyPath).SequenceEqual(Key), "source setup automatically downloads and selects managed key");
            Check(model.Downloads.Count == 0 && model.Library.Count == 0 && model.Installations.Count == 0, "key setup creates no game activity");
            Check(model.ManagedOrbitKeys.Single().Description.Contains("v1.2.3") && model.ManagedOrbitKeys.Single().Description.Contains("SHA-256"), "key descriptions visible");

            var manual = Path.Combine(root, "manual.hex"); await File.WriteAllTextAsync(manual, new string('b', 64));
            var imported = await model.ImportOrbitKeyAsync(manual);
            dropdown.SelectedValue = imported.FilePath;
            Check(((TextBox)window.FindName("SourceKeyBox")).Text == imported.FilePath, "dropdown selects imported key path");
            ((Button)window.FindName("SaveSourceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => source.KeyFilePath == imported.FilePath);
            var calls = fixture.AssetCalls;
            ((Button)window.FindName("DownloadSourceKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => ((StackPanel)window.FindName("SourceEditorPanel")).IsEnabled);
            Check(fixture.AssetCalls == calls && source.KeyFilePath == imported.FilePath && ((TextBox)window.FindName("SourceKeyBox")).Text == imported.FilePath &&
                File.ReadAllBytes(originalKeyPath).SequenceEqual(Key), "redownload reuses stored release without replacing selected manual key");
            fixture.Bundle = BuildBundle(text: new string('c', 64));
            ((Button)window.FindName("DownloadSourceKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => ((StackPanel)window.FindName("SourceEditorPanel")).IsEnabled);
            Check(model.ManagedOrbitKeys.Count == 3 && source.KeyFilePath == imported.FilePath &&
                ((TextBox)window.FindName("SourceKeyBox")).Text == imported.FilePath && File.ReadAllBytes(originalKeyPath).SequenceEqual(Key),
                "new published key remains available without replacing selected or older keys");
            fixture.ChangeMetadata = o => o["assets"]![0]!["digest"] = "sha256:" + new string('0', 64);
            fixture.Download = async token => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); };
            ((Button)window.FindName("DownloadSourceKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => model.IsFetchingOrbitKey);
            Check(!((StackPanel)window.FindName("SourceEditorPanel")).IsEnabled, "source edits frozen during download");
            ((Button)window.FindName("CancelSourceKeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !model.IsFetchingOrbitKey && ((StackPanel)window.FindName("SourceEditorPanel")).IsEnabled);
            Check(source.KeyFilePath == imported.FilePath && model.ManagedOrbitKeys.Count == 3 && model.Status.Contains("cancelled"), "cancel preserves keys and source selection");
            Check(store.Load().Sources.Single().KeyFilePath == imported.FilePath && File.ReadAllText(manual) == new string('b', 64), "saved selection and original manual file preserved");
            model.AddLiveOrbitSource("Disabled", LiveOrbitCatalog.OfficialUrl, "", false, true);
            var disabled = model.SelectedSource!; var requests = fixture.Calls;
            ((Button)window.FindName("SaveSourceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => ((StackPanel)window.FindName("SourceEditorPanel")).IsEnabled);
            Check(fixture.Calls == requests && disabled.KeyFilePath.Length == 0, "disabled setup does not download a key");
            model.RemoveSource(disabled);
        }
        finally { window.Close(); }
        try
        {
            using var restarted = new MainViewModel(store);
            Check(restarted.ManagedOrbitKeys.Count == 3 && restarted.SelectedSource is null && restarted.Sources.Single().KeyFilePath.Contains("keys"), "inventory and source key survive restart");
            Console.WriteLine("PASS managed Orbit key dropdown, automatic setup, manual selection, reuse, cancellation and restart");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Key UI operation did not finish."); await Task.Delay(20); }
    }
}
