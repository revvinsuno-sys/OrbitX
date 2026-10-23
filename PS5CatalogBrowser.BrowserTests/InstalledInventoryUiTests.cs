using System.IO;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class InstalledInventoryUiTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5InventoryUi-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [], InstalledGamesRoot = installed, DownloadRoot = Path.Combine(root, "downloads") });
        var model = new MainViewModel(store);
        try
        {
            var folder = Path.Combine(installed, "external-game"); Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
            File.WriteAllText(Path.Combine(folder, "eboot.bin"), "synthetic");
            File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), """{"titleId":"PPSA19015","titleName":"External inventory fixture"}""");
            await Wait(() => model.LibraryView.Cast<LocalLibraryEntry>().Any(item => item.ContentPath == folder), "watcher registers an externally added game");
            Check(store.Load().Library.Single().ContentPath == folder && model.LibraryDisplayTitle(model.Library.Single()) == "External inventory fixture", "discovered game is persisted with inspected metadata");
            var renamed = Path.Combine(installed, "renamed-game"); Directory.Move(folder, renamed);
            await Wait(() => model.LibraryView.Cast<LocalLibraryEntry>().Select(item => item.ContentPath).SequenceEqual(new[] { renamed }), "external rename replaces the active path without duplicate cards");
            Directory.Delete(renamed, true);
            await Wait(() => model.LibraryView.IsEmpty, "external deletion removes the game from the visible library");
            Check(model.Settings.Library.Count == 2, "missing provenance is retained as history");
            var wrapper = Path.Combine(installed, "notes-only"); Directory.CreateDirectory(wrapper); File.WriteAllText(Path.Combine(wrapper, "Note.txt"), "preserve");
            await model.ReconcileInstalledLibraryAsync();
            Check(model.LibraryView.IsEmpty && !model.Library.Any(item => item.ContentPath == wrapper), "notes-only folders do not become games");
            var otherRoot = Path.Combine(root, "other-installed"); Directory.CreateDirectory(otherRoot);
            model.InstalledGamesRoot = otherRoot;
            var newGame = Path.Combine(otherRoot, "new-root-game"); Directory.CreateDirectory(newGame); File.WriteAllText(Path.Combine(newGame, "eboot.bin"), "synthetic");
            await Wait(() => model.LibraryView.Cast<LocalLibraryEntry>().SingleOrDefault()?.ContentPath == newGame, "changing Installed rebinds the watcher and inventory");
            Directory.Delete(otherRoot, true);
            await Wait(() => model.LibraryView.IsEmpty, "removing the configured Installed directory clears active membership");
            Directory.CreateDirectory(newGame); File.WriteAllText(Path.Combine(newGame, "eboot.bin"), "returning synthetic game");
            await Wait(() => model.LibraryView.Cast<LocalLibraryEntry>().SingleOrDefault()?.ContentPath == newGame, "recreating Installed rebinds monitoring and restores the active path");
            Console.WriteLine("PASS live Installed synchronization: external add, rename, deletion, metadata, persistent history, notes exclusion and configured-root changes");
        }
        finally { model.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        async Task Wait(Func<bool> ready, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline) { if (ready()) return; await Task.Delay(100); }
            throw new Exception("Installed inventory timeout: " + label + " / " + model.Status);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
