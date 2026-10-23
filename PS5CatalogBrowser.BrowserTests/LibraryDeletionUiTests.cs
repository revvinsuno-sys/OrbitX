using System.IO;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class LibraryDeletionUiTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5LibraryDeleteUi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        MainWindow? window = null;
        try
        {
            var file = Path.Combine(root, "fixture.htm"); File.WriteAllText(file, "synthetic file");
            var connected = Path.Combine(root, "fixture_files"); Directory.CreateDirectory(connected); File.WriteAllText(Path.Combine(connected, "keep.txt"), "unrelated sibling");
            var folder = Path.Combine(root, "fixture-game"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "eboot.bin"), "synthetic game");
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = root,
                Downloads = [new() { Destination = file, State = "Imported" }, new() { Destination = folder, State = "Imported" }],
                Library = [new() { ContentPath = file }, new() { ContentPath = folder }] });
            window = new MainWindow(store) { ShowActivated = false };
            ((Grid)window.Content).Children.OfType<TabControl>().Single().Items.OfType<TabItem>().Single(tab => tab.Header.ToString() == "My Library").IsSelected = true;
            window.Show();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var model = (MainViewModel)window.DataContext;
            var button = (Button)window.FindName("DeleteLibraryButton");
            Check(button.Content.ToString() == "Delete" && button.IsVisible && button.ActualWidth > 0 && !button.IsEnabled, "Delete button is visible in My Library and disabled without selection");
            var deletable = model.Library.ToArray();
            var extractingPath = Path.Combine(root, "active-extraction.rar"); File.WriteAllText(extractingPath, "synthetic active archive");
            var extracting = new LocalLibraryEntry { ContentPath = extractingPath, Extraction = "Extracting" };
            model.Library.Add(extracting); model.Settings.Library.Add(extracting);
            Check(!model.CanDeleteItem(extracting), "the extracting item cannot be deleted");
            try { model.PrepareLibraryDeletion(extracting); throw new Exception("Active extraction deletion accepted"); }
            catch (InvalidOperationException) { }
            var overlapping = new LocalLibraryEntry { ContentPath = extractingPath }; model.Library.Add(overlapping); model.Settings.Library.Add(overlapping);
            try { model.PrepareLibraryDeletion(overlapping); throw new Exception("Shared extraction source deletion accepted"); }
            catch (IOException) { }
            model.Library.Remove(overlapping); model.Settings.Library.Remove(overlapping);
            foreach (var item in deletable)
            {
                model.SelectedLibraryEntry = item;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(button.IsEnabled, "Delete button is enabled for a selected item");
                var confirmed = model.PrepareLibraryDeletion(item);
                var download = model.Downloads.Single(job => job.Destination == item.ContentPath); model.SelectedJob = download;
                Check(File.Exists(file) || Directory.Exists(folder), "Preparation does not delete files");
                model.DeleteLibraryEntry(item, confirmed); // Real Windows Recycle Bin, synthetic fixtures only.
                Check(!File.Exists(item.ContentPath) && !Directory.Exists(item.ContentPath) && !store.Load().Library.Any(entry => entry.Id == item.Id), "Confirmed deletion removes local content and persists library removal");
                Check(model.SelectedLibraryEntry is null, "Deleted selection is cleared");
                Check(!model.Downloads.Contains(download) && model.SelectedJob != download && store.Load().Downloads.Single(job => job.Id == download.Id).IsCleared,
                    "successful file/folder deletion immediately removes and deselects its download and persists removal");
                Check(File.ReadAllText(Path.Combine(connected, "keep.txt")) == "unrelated sibling", "Windows must never delete connected sibling folders");
                Check(File.ReadAllText(extractingPath) == "synthetic active archive" && extracting.Extraction == "Extracting", "unrelated deletion preserves the extracting item and its archive");
            }
            model.Library.Remove(extracting); model.Settings.Library.Remove(extracting); store.Save(model.Settings);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Check(!button.IsEnabled && model.Library.Count == 0, "Button disables after removing the selected item");
            window.Close(); window = null;
            using var reopened = new MainViewModel(store);
            Check(reopened.Library.Count == 0, "Deleted library entries stay removed after restart");
            Check(reopened.Downloads.Count == 0, "deleted downloads stay removed after restart");
            Console.WriteLine("PASS library Delete button bindings, real Windows file/folder recycling and persisted removal/restart");
        }
        finally { window?.Close(); Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
