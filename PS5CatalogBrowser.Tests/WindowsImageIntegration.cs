using PS5CatalogBrowser.Core;

internal static class WindowsImageIntegration
{
    public static async Task RunAsync()
    {
        var executable = OSFMountDependency.Detect();
        if (!OperatingSystem.IsWindows() || executable.Length == 0)
        { Console.WriteLine("SKIP Windows exFAT driver integration: official OSFMount installation is unavailable."); return; }
        var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PS5CatalogBrowser.App", "bin", "Release", "net10.0-windows", "PS5CatalogBrowser.MountHost.exe"));
        var root = Path.Combine(Path.GetTempPath(), "PS5WindowsImageTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "synthetic.exfat"); SyntheticExFat.Create(source);
            var journal = new InstallationJournal(Path.Combine(root, "journal")); var store = new SettingsStore(Path.Combine(root, "settings")); var settings = new AppSettings();
            var job = new InstallationJob { SourcePath = source, InstallationRoot = Path.Combine(root, "Installed"), DeleteOriginal = true };
            var coordinator = new ImageInstallationCoordinator(new OSFMountAdapter(executable, helper), journal,
                installation => { InstalledLibrary.Register(settings, installation); store.Save(settings); return Task.CompletedTask; },
                installation => Task.FromResult(InstalledLibrary.IsRegistered(store.Load(), installation)), _ => false);
            await coordinator.InstallAsync(job, confirmation => Task.FromResult<InstallationChoice?>(new(confirmation.FolderName, true)), null, default);
            if (job.State != InstallationState.Completed || !job.SourceDeleted || File.Exists(source) || !InstalledLibrary.IsRegistered(store.Load(), job))
                throw new Exception("Windows driver integration failed: " + job.StateLabel + ": " + job.Error);
            await ImageFileSystem.VerifyFinalAsync(job.Destination, job, default);
            Console.WriteLine("PASS Windows synthetic exFAT read-only mount, scan, copy, SHA-256 verification, release, persistent library and source deletion");
        }
        finally
        {
            // Integration fixture is isolated; leave evidence if a real driver mount still needs attention.
            var journal = new InstallationJournal(Path.Combine(root, "journal"));
            if (journal.Load().Any(job => job.State == InstallationState.NeedsAttention && job.MountDescription != "Released"))
                Console.WriteLine("Fixture retained for mount reconciliation: " + root);
            else Directory.Delete(root, true);
        }
    }
}
