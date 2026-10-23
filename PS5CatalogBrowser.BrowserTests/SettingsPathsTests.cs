using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class SettingsPathsTests
{
    public static async Task RunAsync()
    {
        await RunnableContentUiTests.RunAsync();
        var root = Path.Combine(Path.GetTempPath(), "PS5SettingsPaths-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings"));
        var originalDownloads = Path.Combine(root, "downloads");
        var installed = Path.Combine(root, "separate-library");
        store.Save(new() { Sources = [], DownloadRoot = originalDownloads, InstalledGamesRoot = installed });
        MainWindow? window = null;
        try
        {
            window = new MainWindow(store) { ShowActivated = false }; window.Show();
            var model = (MainViewModel)window.DataContext;
            var downloadsBox = (TextBox)window.FindName("DownloadRootBox");
            var installedBox = (TextBox)window.FindName("InstalledRootBox");
            var editedDownloads = Path.Combine(root, "new-downloads");
            var editedInstalled = Path.Combine(root, "edited-library");
            downloadsBox.SetCurrentValue(TextBox.TextProperty, editedDownloads);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(store.Load().DownloadRoot == editedDownloads && model.InstalledGamesRoot == installed && store.Load().InstalledGamesRoot == installed,
                "download edit saves without focus change and preserves installed directory");
            installedBox.SetCurrentValue(TextBox.TextProperty, editedInstalled);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(store.Load().InstalledGamesRoot == editedInstalled && model.DownloadRoot == editedDownloads, "installed edit saves independently without focus change");
            window.Close(); window = null;
            using (var restarted = new MainViewModel(store))
                Check(restarted.DownloadRoot == editedDownloads && restarted.InstalledGamesRoot == editedInstalled, "both directory edits survive restart");

            // Legacy/unconfigured settings must not derive an installation directory from Downloads.
            store.Save(new() { Sources = [], DownloadRoot = originalDownloads });
            using (var modelWithoutInstalledPath = new MainViewModel(store))
            {
                Check(modelWithoutInstalledPath.InstalledGamesRoot == "" && !modelWithoutInstalledPath.CanInstallFromFile, "missing installed directory stays blank and installation is unavailable");
                modelWithoutInstalledPath.DownloadRoot = editedDownloads;
                Check(modelWithoutInstalledPath.InstalledGamesRoot == "" && store.Load().InstalledGamesRoot == "", "download edit cannot invent or persist an Installed suffix");
            }
            Check(!Directory.Exists(originalDownloads) && !Directory.Exists(editedDownloads) && !Directory.Exists(installed) && !Directory.Exists(editedInstalled),
                "settings edits do not create or move content directories");
            Console.WriteLine("PASS independent download/installed paths: WPF immediate save, restart, missing-path behavior and no filesystem relocation");
        }
        finally { window?.Close(); Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
