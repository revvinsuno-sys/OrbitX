using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class InstalledInventoryTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5Inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
            string Game(string name)
            {
                var folder = Path.Combine(installed, name); Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
                File.WriteAllText(Path.Combine(folder, "eboot.bin"), "synthetic executable");
                File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), """{"titleId":"PPSA19015","titleName":"Inspected inventory game"}""");
                return folder;
            }
            var orphan = Game("untracked"); var verified = Game("verified"); var changed = Game("changed");
            var notes = Path.Combine(installed, "notes-wrapper"); Directory.CreateDirectory(notes); File.WriteAllText(Path.Combine(notes, "Note.txt"), "keep");
            var source = new LocalLibraryEntry { ContentPath = Path.Combine(root, "downloads", "source.exfat"), Title = "Source registration" };
            var missing = new LocalLibraryEntry { ContentPath = Path.Combine(installed, "missing"), ContentMove = new() { Phase = "Completed" } };
            var settings = new AppSettings { DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = installed, Sources = [],
                Library = [source, missing, new() { ContentPath = notes }], Downloads = [new() { Destination = source.ContentPath, State = "Missing" }] };
            var journals = new List<InstallationJob>();
            foreach (var folder in new[] { verified, changed })
            {
                var scan = await ImageFileSystem.ScanAsync(folder, default);
                var files = new List<ImageFile>();
                foreach (var file in scan.Files) files.Add(file with { Sha256 = await ImageFileSystem.HashAsync(Path.Combine(folder, file.RelativePath), default) });
                journals.Add(new() { SourcePath = source.ContentPath, Destination = folder, InstallationRoot = installed, Files = files,
                    Directories = scan.Directories, Title = "Verified fixture", Verified = true, Registered = true, State = InstallationState.Completed });
            }
            File.WriteAllText(Path.Combine(changed, "eboot.bin"), "changed bytes");
            var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(settings);
            var original = JsonSerializer.Serialize(settings);
            store.BeforeSave = _ => throw new IOException("Synthetic persistence fault");
            try { await InstalledLibraryInventory.ReconcileAsync(settings, journals, store.Save); throw new Exception("Failed reconciliation save accepted"); }
            catch (IOException) { Check(JsonSerializer.Serialize(settings) == original, "failed save leaves all live records unchanged"); }
            store.BeforeSave = null;
            var report = await InstalledLibraryInventory.ReconcileAsync(settings, journals, store.Save);
            Check(report.Present.Count == 3 && report.Added.Count == 2 && report.Restored.Count == 1 && report.Missing.Count == 1 && report.Excluded.Count == 1,
                "disk inventory distinguishes discovered games, restored registration, missing history and notes-only wrapper");
            var recovered = settings.Library.Single(item => item.ContentPath == verified);
            Check(recovered.Id == source.Id && recovered.InstalledGame?.InstallationId == journals[0].Id, "verified journal restores original source registration and evidence");
            Check(settings.Library.Single(item => item.ContentPath == orphan).Integrity.Contains("not verified") && settings.Library.Single(item => item.ContentPath == changed).InstalledGame is null,
                "external and changed folders never gain invented verification");
            Check(settings.Library.Contains(missing) && settings.Downloads.Single().State == "Missing" && File.ReadAllText(Path.Combine(notes, "Note.txt")) == "keep",
                "reconciliation preserves historical records, downloads and non-game files");
            var again = await InstalledLibraryInventory.ReconcileAsync(settings, journals, _ => throw new Exception("Unchanged inventory should not save"));
            Check(again.Added.Count + again.Restored.Count == 0, "repeated reconciliation is idempotent");
            Game("missing");
            await InstalledLibraryInventory.ReconcileAsync(settings, journals, store.Save);
            Check(missing.Classification == "Ready for launch attempt", "a returning folder clears its obsolete missing-content classification");
            var late = Game("late");
            var deferred = await InstalledLibraryInventory.ReconcileAsync(settings, journals, store.Save, canCommit: () => false);
            Check(deferred.Deferred && !settings.Library.Any(item => item.ContentPath == late), "an operation starting during inspection defers the commit");
            var lateSettingsChange = new LocalLibraryEntry { ContentPath = Path.Combine(root, "concurrent-record") };
            await InstalledLibraryInventory.ReconcileAsync(settings, journals, store.Save, canCommit: () => { settings.Library.Add(lateSettingsChange); return true; });
            Check(store.Load().Library.Any(item => item.Id == lateSettingsChange.Id), "commit preserves state added after asynchronous inspection");
            Console.WriteLine("PASS Installed inventory: exact disk membership, verified journal recovery, unverified discovery, missing history, ghost wrappers, save rollback, idempotence and concurrent updates");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
