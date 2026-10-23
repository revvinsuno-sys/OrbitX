using PS5CatalogBrowser.Core;

var store = new SettingsStore();
using var ownership = store.AcquireApplicationOwnership();
if (args.SequenceEqual(new[] { "--reconcile-library" }))
{
    var current = store.LoadForStartup();
    var journals = new InstallationJournal(Path.Combine(store.DirectoryPath, "installations")).Load();
    var report = await InstalledLibraryInventory.ReconcileAsync(current, journals, store.Save);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Deferred ? 1 : 0;
}
var apply = args.SequenceEqual(new[] { "--apply" });
if (args.Length > 0 && !apply) throw new ArgumentException("Use --apply, or no arguments for a read-only plan.");
var settings = apply ? store.LoadForStartup() : store.Load();
var failures = 0;
foreach (var item in settings.Library.Where(item => item.ContentMove is { Phase: "Completed", Directory: true } && item.InstalledGame is null && item.PackageInstallations.Count == 0).ToArray())
{
    try
    {
        if (!Directory.Exists(item.ContentPath)) { Console.WriteLine("UNAVAILABLE " + item.ContentPath); continue; }
        var plan = await InstalledFolderLayout.PlanAsync(item.ContentPath, default);
        if (plan is null) { Console.WriteLine("UNCHANGED " + item.ContentPath); continue; }
        Console.WriteLine("FLATTEN " + item.ContentPath + " <- " + plan.GameRoot);
        if (apply)
        {
            if (!await RunnableContentMove.NormalizeInstalledAsync(settings, item, store.Save)) throw new IOException("Installation is not eligible for normalization.");
            Console.WriteLine("VERIFIED " + item.ContentPath);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("FAILED " + item.ContentPath + ": " + ex.Message); failures++; }
}
return failures == 0 ? 0 : 1;
