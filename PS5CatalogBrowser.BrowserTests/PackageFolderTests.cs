using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class PackageFolderTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5PackageFolders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var retain in new[] { true, false }) await RunFolderAsync(Path.Combine(root, retain.ToString()), retain);
            Console.WriteLine("PASS package folders: Orbit multi-selection, PS4/PS5 and DLC installation, pending-only Install, multiple launch choices, retention, restart, failed PKG preservation and full reinspection");
        }
        finally
        {
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
                try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
            if (Directory.Exists(root)) throw new IOException("Package folder fixture remains in use: " + root);
        }
    }

    private static async Task RunFolderAsync(string root, bool retain)
    {
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Interface = "Orbit Store", Sources = [], DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = Path.Combine(root, "installed"), RetainOriginalImage = retain });
        var input = Path.Combine(root, "downloads", "Bundle"); Directory.CreateDirectory(input);
        PackageArchiveTests.BuildPackage(Path.Combine(input, "game-ps4.pkg"), false);
        PackageArchiveTests.BuildPackage(Path.Combine(input, "game-ps5.pkg"), true);
        PackageArchiveTests.BuildPackage(Path.Combine(input, "dlc.pkg"), !retain, game: false);
        Directory.CreateDirectory(Path.Combine(input, "game-ps4")); File.WriteAllText(Path.Combine(input, "game-ps4", "keep.txt"), "existing folder preserved");
        var main = new MainWindow(store) { ShowActivated = false };
        var model = (MainViewModel)main.DataContext;
        string parentId;
        try
        {
            main.Show(); var surface = (ContentControl)main.FindName("OrbitSurface");
            await Wait(() => surface.Content is OrbitFrontendHost && !model.IsMovingContent);
            var host = (OrbitFrontendHost)surface.Content; await host.InitializeAsync(model);
            await model.ScanDownloadsAsync();
            var item = model.LibraryView.Cast<LocalLibraryEntry>().Single(); parentId = item.Id;
            Check(item.PendingPackagePaths.Count == 3 && item.LaunchPaths.Count == 0 && model.Downloads.Count == 0, "scan registers one package folder, never individual PKG downloads");
            using var api = new OrbitFrontendApi(model);
            Check(api.LibraryActions(item).Contains("installLibrary") && !api.LibraryActions(item).Contains("launch"), "package-only folder exposes Install and no Launch");
            var unselected = Path.Combine(item.ContentPath, "game-ps5.pkg"); var digest = await ImageFileSystem.HashAsync(unselected, default);
            host.NavigatePage("Library");
            await Click("installLibrary", item.Id);
            await Wait(() => main.OwnedWindows.OfType<FolderContentSelectionDialog>().Any());
            var picker = main.OwnedWindows.OfType<FolderContentSelectionDialog>().Single();
            Check(picker.Choices.Count == 3 && !picker.ConfirmButton.IsEnabled, "Install dialog lists all pending PKGs and requires a selection");
            picker.Choices.Single(choice => Path.GetFileName(choice.Path) == "game-ps4.pkg").Control.IsChecked = true;
            picker.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !model.IsInstalling && model.Library.Single(entry => entry.Id == parentId).PackageInstallations.Count == 1);
            item = model.Library.Single(entry => entry.Id == parentId);
            Check(item.PendingPackagePaths.Count == 2 && item.LaunchPaths.Count == 1 && File.Exists(Path.Combine(item.ContentPath, "game-ps4.pkg")) == retain,
                "selected PKG installs, retention honoured, only two unselected packages remain");
            Check(await ImageFileSystem.HashAsync(unselected, default) == digest, "unselected package remains byte-identical");
            Check(File.ReadAllText(Path.Combine(item.ContentPath, "game-ps4", "keep.txt")) == "existing folder preserved" && Directory.Exists(Path.Combine(item.ContentPath, "game-ps4 (1)")),
                "existing destination folder is preserved and installation uses an unused sibling folder");
            await Click("installLibrary", item.Id);
            await Wait(() => main.OwnedWindows.OfType<FolderContentSelectionDialog>().Any());
            picker = main.OwnedWindows.OfType<FolderContentSelectionDialog>().Single();
            Check(picker.Choices.Count == 2 && picker.Choices.All(choice => Path.GetFileName(choice.Path) != "game-ps4.pkg"), "Install dialog excludes previously extracted package even when retained");
            foreach (var choice in picker.Choices) choice.Control.IsChecked = true;
            picker.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !model.IsInstalling && model.Library.Single(entry => entry.Id == parentId).PackageInstallations.Count == 3);
            item = model.Library.Single(entry => entry.Id == parentId);
            Check(item.PendingPackagePaths.Count == 0 && item.LaunchPaths.Count == 2 && !api.LibraryActions(item).Contains("installLibrary") && api.LibraryActions(item).Contains("launch"),
                "all PKGs extracted hides Install, DLC installs without eboot, and two game folders expose Launch");
            Check(model.LibraryView.Cast<LocalLibraryEntry>().Count() == 1 && item.PackageInstallations.All(package => Directory.Exists(package.Directory)), "installed packages stay grouped under one library item");
            await model.ReinspectAsync(item);
            Check(model.PrepareLibraryDeletion(item).Count == 1, "grouped package folders retain one exact-path deletion target");
            await Click("launch", item.Id);
            await Wait(() => main.OwnedWindows.OfType<FolderContentSelectionDialog>().Any());
            picker = main.OwnedWindows.OfType<FolderContentSelectionDialog>().Single();
            Check(picker.Choices.Count == 2 && picker.Choices.All(choice => item.LaunchPaths.Contains(choice.Path)), "Launch dialog lists game folders and excludes DLC-only output");
            picker.Choices[0].Control.IsChecked = true; picker.Choices[1].Control.IsChecked = true;
            Check(picker.Choices.Count(choice => choice.Control.IsChecked == true) == 1, "Launch selects exactly one extracted game folder");
            picker.Close(); // Never run synthetic executable bytes.
            var launch = item.LaunchPaths[0]; var executable = Path.Combine(root, "kyty_emulator.exe"); File.WriteAllText(executable, "synthetic non-executable");
            Check(KytyLauncher.BuildStartInfo(executable, launch).ArgumentList.Last() == launch, "launcher receives chosen game root rather than bundle root");
            var changed = Path.Combine(launch, "data.txt"); var original = File.ReadAllBytes(changed);
            File.WriteAllBytes(changed, [0]);
            try { await model.ReinspectAsync(item); throw new Exception("tampered package passed reinspection"); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            Check(!api.LibraryActions(item).Contains("launch"), "failed installed-file verification disables bundle Launch");
            File.WriteAllBytes(changed, original); await model.ReinspectAsync(item);
            if (retain)
            {
                var pkg = Path.Combine(item.ContentPath, "game-ps4.pkg"); var bytes = File.ReadAllBytes(pkg); var edited = bytes.ToArray(); edited[^1] ^= 1;
                File.WriteAllBytes(pkg, edited); await model.RefreshFolderActionsAsync(item);
                Check(item.PendingPackagePaths.SequenceEqual([pkg]), "changed retained package is offered for extraction again");
                File.WriteAllBytes(pkg, bytes); await model.RefreshFolderActionsAsync(item);
            }
            var invalid = Path.Combine(item.ContentPath, "invalid.pkg"); File.WriteAllText(invalid, "invalid package");
            await model.RefreshFolderActionsAsync(item); await model.InstallFolderPackagesAsync(item, [invalid]);
            Check(File.Exists(invalid) && item.PackageInstallations.Count == 3 && model.CanInstallItem(item), "failed package preserves source and remains available to Install");
            File.Delete(invalid); await model.RefreshFolderActionsAsync(item);

            async Task Click(string action, string id)
            {
                var selector = "[data-windows-id='" + id + "'] [data-windows-action='" + action + "']";
                await WaitScript(host, "!!document.querySelector(\"" + selector + "\")");
                await host.Browser.ExecuteScriptAsync("document.querySelector(\"" + selector + "\").click()");
            }
        }
        finally { main.Close(); }
        using var restarted = new MainViewModel(store); await restarted.RefreshFolderActionsAsync();
        var saved = restarted.Library.Single(item => item.Id == parentId);
        Check(saved.PackageInstallations.Count == 3 && saved.PendingPackagePaths.Count == 0 && saved.LaunchPaths.Count == 2, "package installation records and remaining/launch state survive restart");
        await restarted.ReinspectAsync(saved);
    }
    private static async Task Wait(Func<bool> predicate)
    { var until = DateTime.UtcNow.AddSeconds(30); while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("Package folder UI did not settle."); await Task.Delay(40); } }
    private static async Task WaitScript(OrbitFrontendHost host, string script)
    { var until = DateTime.UtcNow.AddSeconds(30); while (await host.Browser.ExecuteScriptAsync(script) != "true") { if (DateTime.UtcNow > until) throw new TimeoutException(script); await Task.Delay(40); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
