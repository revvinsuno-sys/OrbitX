using System.IO;
using System.Windows.Threading;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    private List<string> installedInventoryPaths = [];
    private FileSystemWatcher? installedWatcher;
    private readonly DispatcherTimer installedInventoryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool installedInventoryDirty = true, reconcilingInstalledInventory;
    private DateTimeOffset installedChangedAt = DateTimeOffset.MinValue;
    private int installedWatchGeneration;
    private bool InstalledInventoryBusy => IsInstalling || IsExtracting || IsMovingContent || IsDeletingLibraryEntry || IsClearingDownloads || isPackageBatch;

    private void ReadInstalledMembership()
    {
        try { installedInventoryPaths = InstalledLibraryInventory.Scan(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status = "Cannot inspect Installed: " + ex.Message; }
    }

    private void WatchInstalledFolder()
    {
        installedWatchGeneration++;
        installedWatcher?.Dispose(); installedWatcher = null;
        installedInventoryDirty = true; installedChangedAt = DateTimeOffset.MinValue;
        if (!Path.IsPathFullyQualified(InstalledGamesRoot) || !Directory.Exists(InstalledGamesRoot)) return;
        try
        {
            var generation = installedWatchGeneration;
            var watcher = new FileSystemWatcher(InstalledGamesRoot) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            void Dirty()
            {
                if (disposed || ui.HasShutdownStarted) return;
                ui.BeginInvoke(new Action(() =>
                {
                    if (disposed || generation != installedWatchGeneration) return;
                    installedInventoryDirty = true; installedChangedAt = DateTimeOffset.UtcNow;
                }));
            }
            watcher.Created += (_, _) => Dirty(); watcher.Deleted += (_, _) => Dirty();
            watcher.Renamed += (_, _) => Dirty(); watcher.Changed += (_, _) => Dirty();
            watcher.Error += (_, _) =>
            {
                if (!disposed && !ui.HasShutdownStarted)
                    ui.BeginInvoke(new Action(() => { if (!disposed && generation == installedWatchGeneration) WatchInstalledFolder(); }));
            };
            installedWatcher = watcher; watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Status = "Installed folder monitoring is unavailable: " + ex.Message; }
    }

    private async void InstalledInventoryTimer_Tick(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (installedWatcher is null && Directory.Exists(InstalledGamesRoot) || installedWatcher is not null && !Directory.Exists(InstalledGamesRoot)) WatchInstalledFolder();
        if (installedInventoryDirty && DateTimeOffset.UtcNow - installedChangedAt >= TimeSpan.FromSeconds(2))
            await ReconcileInstalledLibraryAsync();
    }

    public async Task<InstalledInventoryResult?> ReconcileInstalledLibraryAsync()
    {
        if (disposed || reconcilingInstalledInventory || InstalledInventoryBusy) return null;
        reconcilingInstalledInventory = true; installedInventoryDirty = false;
        var generation = installedWatchGeneration;
        try
        {
            var result = await InstalledLibraryInventory.ReconcileAsync(Settings, Installations.ToArray(), store.Save, catalogueLifetime.Token,
                () => !disposed && !InstalledInventoryBusy && generation == installedWatchGeneration);
            if (result.Deferred) { installedInventoryDirty = true; return result; }
            foreach (var saved in Settings.Library)
            {
                var existing = Library.FirstOrDefault(item => item.Id == saved.Id);
                if (existing is null) Library.Add(saved);
                else if (!ReferenceEquals(existing, saved))
                {
                    Library[Library.IndexOf(existing)] = saved;
                    if (SelectedLibraryEntry == existing) SelectedLibraryEntry = saved;
                }
            }
            Changed(nameof(Library));
            if (SelectedLibraryEntry is not null && !LibraryView.Contains(SelectedLibraryEntry)) SelectedLibraryEntry = null;
            await RefreshFolderActionsAsync();
            if (result.Issues.Count > 0) Status = "Installed folder synchronized with items needing attention: " + string.Join("; ", result.Issues);
            else if (result.Added.Count + result.Restored.Count > 0) Status = $"My Library synchronized: {result.Restored.Count} verified registrations restored; {result.Added.Count} discovered folders added.";
            return result;
        }
        catch (OperationCanceledException) when (disposed || catalogueLifetime.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { installedInventoryDirty = true; installedChangedAt = DateTimeOffset.UtcNow.AddSeconds(8); Status = "Installed folder synchronization failed: " + ex.Message; return null; }
        finally { reconcilingInstalledInventory = false; }
    }
}
