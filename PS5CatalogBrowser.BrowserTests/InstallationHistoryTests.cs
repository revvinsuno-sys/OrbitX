using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class InstallationHistoryTests
{
    public static async Task RunAsync()
    {
        await SettingsPathsTests.RunAsync();
        var root = Path.Combine(Path.GetTempPath(), "PS5InstallationHistory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        try
        {
            var source = Path.Combine(root, "original.exfat"); File.WriteAllText(source, "synthetic retained image");
            var destination = Path.Combine(root, "installed"); Directory.CreateDirectory(Path.Combine(destination, "sce_sys"));
            File.WriteAllBytes(Path.Combine(destination, "eboot.bin"), SyntheticExFat.TestExecutable());
            File.WriteAllText(Path.Combine(destination, "sce_sys", "param.json"), "{\"titleId\":\"PPSA00001\",\"titleName\":\"History fixture\"}");
            var scan = await ImageFileSystem.ScanAsync(destination, default);
            var files = new List<ImageFile>();
            foreach (var file in scan.Files) files.Add(file with { Sha256 = await ImageFileSystem.HashAsync(Path.Combine(destination, file.RelativePath), default) });
            var completed = new InstallationJob { Title = "History fixture", TitleId = "PPSA00001", SourcePath = source, Destination = destination,
                State = InstallationState.CompletedOriginalRetained, Verified = true, Registered = true, Files = files, Directories = scan.Directories };
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var journal = new InstallationJournal(Path.Combine(store.DirectoryPath, "installations"));
            var settings = new AppSettings { Sources = [], InstalledGamesRoot = root };
            InstalledLibrary.Register(settings, completed); store.Save(settings); journal.Save(completed);
            foreach (var state in new[] { InstallationState.Completed, InstallationState.Failed, InstallationState.Cancelled, InstallationState.NeedsAttention })
                journal.Save(new() { Title = state.ToString(), SourcePath = source, State = state });

            window = new MainWindow(store) { ShowActivated = false }; window.Show();
            ((TabItem)window.FindName("InstallationsTab")).IsSelected = true; window.UpdateLayout();
            var model = (MainViewModel)window.DataContext;
            var list = (ListBox)window.FindName("InstallationList"); var clear = (Button)window.FindName("ClearInstallationsButton");
            model.SelectedLibraryEntry = model.Library.Single();
            Check(list.Items.Count == 5 && clear.IsEnabled && model.CanLaunchSelected, "Clear all is available without altering completed library eligibility");

            // Use a real verification operation to exercise the active-operation guard without mounting anything.
            var verifying = model.ReinspectAsync(model.SelectedLibraryEntry);
            Check(model.IsInstalling && !model.CanClearInstallations && !clear.IsEnabled, "Clear all is disabled during active verification");
            try { model.ClearAllInstallations(); throw new Exception("Active operation history was cleared"); }
            catch (InvalidOperationException) { }
            await verifying;
            Check(model.CanLaunchSelected && clear.IsEnabled, "Verification still works before history clearing");

            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(list.Items.Count == 0 && model.InstallationView.IsEmpty && model.SelectedInstallation is null && !clear.IsEnabled,
                "Clear all empties visible rows and selection and disables the empty-state action");
            Check(model.Installations.Count == 5 && journal.Load().All(job => job.IsCleared) && model.InstallationProgressText == "No installation active",
                "History stays cleared on disk while retaining canonical verification/recovery records");
            Check(File.ReadAllText(source) == "synthetic retained image" && File.ReadAllBytes(Path.Combine(destination, "eboot.bin")).SequenceEqual(SyntheticExFat.TestExecutable()) &&
                store.Load().Library.Count == 1 && model.CanLaunchSelected, "History clearing preserves source, installed content and library registration");
            window.Close(); window = null;

            using (var reopened = new MainViewModel(store))
            {
                reopened.SelectedLibraryEntry = reopened.Library.Single();
                Check(reopened.InstallationView.IsEmpty && reopened.SelectedInstallation is null && reopened.CanLaunchSelected, "Cleared history stays hidden after restart without revoking launch");
                await reopened.ReinspectAsync(reopened.SelectedLibraryEntry);
                Check(reopened.CanLaunchSelected && reopened.InstallationView.IsEmpty, "Cleared manifest remains available to full content reinspection");
            }

            var cleanupSettings = store.Load();
            var download = new DownloadJob { Destination = source, State = "Downloaded", DownloadedBytes = new FileInfo(source).Length };
            var otherPath = Path.Combine(root, "unrelated.pkg"); File.WriteAllText(otherPath, "other");
            cleanupSettings.Downloads.Add(download);
            cleanupSettings.Downloads.Add(new() { Destination = otherPath, State = "Downloaded", DownloadedBytes = new FileInfo(otherPath).Length });
            InstalledLibrary.UpdateCleanup(cleanupSettings, completed);
            Check(!download.IsCleared, "retained installation source stays in Downloads");
            File.Delete(source); completed.SourceDeleted = true; completed.State = InstallationState.Completed;
            completed.CleanupResult = "Original deleted in synthetic cleanup fixture";
            InstalledLibrary.UpdateCleanup(cleanupSettings, completed); store.Save(cleanupSettings); journal.Save(completed);
            using (var afterCleanup = new MainViewModel(store))
                Check(afterCleanup.Downloads.Count == 1 && afterCleanup.Downloads.Single().Destination == otherPath &&
                    afterCleanup.Library.Count == 1 && Directory.Exists(afterCleanup.Library.Single().ContentPath),
                    "deleted installation source stays hidden after restart while unrelated downloads and installed library remain");

            // A journal write failure must keep the unsaved row visible rather than losing its record.
            var failedStore = new SettingsStore(Path.Combine(root, "save-fault")); failedStore.Save(new() { Sources = [] });
            var failedJournal = new InstallationJournal(Path.Combine(failedStore.DirectoryPath, "installations"));
            var failed = new InstallationJob { State = InstallationState.NeedsAttention, SourcePath = source }; failedJournal.Save(failed);
            using (var retry = new MainViewModel(failedStore))
            using (var locked = new FileStream(Path.Combine(failedStore.DirectoryPath, "installations", failed.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { retry.ClearAllInstallations(); throw new Exception("Locked journal clear unexpectedly succeeded"); }
                catch (IOException) { }
                Check(!retry.InstallationView.IsEmpty && !retry.Installations.Single().IsCleared && !failedJournal.Load().Single().IsCleared,
                    "Failed persistence rolls back the row's cleared flag in memory and on disk");
            }
            Console.WriteLine("PASS installation Clear all: WPF button, active guard, persisted hiding, preserved files/library/manifests, restart/reinspection and save failure");
        }
        finally { window?.Close(); Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
