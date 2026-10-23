using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    public async Task RefreshFolderActionsAsync(LocalLibraryEntry? selected = null)
    {
        if (disposed || activeInstallation is not null || IsExtracting || IsMovingContent) return;
        foreach (var item in Library.ToArray().Where(item => (selected is null || item.Id == selected.Id) && IsInstalledLibraryItem(item) && Directory.Exists(item.ContentPath)))
        {
            item.InspectedTitle = ""; item.InspectedTitleId = "";
            try
            {
                var actions = await LibraryPackageFolder.InspectAsync(item, Installations.ToArray(), catalogueLifetime.Token);
                if (!Library.Contains(item)) continue;
                item.PendingPackagePaths = actions.Packages; item.LaunchPaths = actions.LaunchPaths;
                var identityRoots = actions.LaunchPaths.Where(candidate => actions.LaunchPaths.All(other => ImageFileSystem.SamePath(candidate, other) ||
                    Path.GetRelativePath(candidate, other).Equals("decrypted", StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
                if (identityRoots.Length == 1)
                {
                    try { (item.InspectedTitle, item.InspectedTitleId) = await ImageFileSystem.ReadGameMetadataAsync(identityRoots[0], catalogueLifetime.Token); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* Optional display metadata does not change launch eligibility. */ }
                }
            }
            catch (OperationCanceledException) when (catalogueLifetime.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { item.PendingPackagePaths = []; item.LaunchPaths = []; item.Classification = "Folder needs attention: " + ex.Message; }
            item.FolderActionsInspected = true;
        }
        Changed(nameof(Library)); NotifyInstallActions();
    }

    public async Task InstallFolderPackagesAsync(LocalLibraryEntry folder, IReadOnlyCollection<string> selected)
    {
        if (IsInstalling || !CanInstallFromFile || !Library.Contains(folder) || folder.DeletionTargets is not null) throw new IOException("Wait for the active operation before installing packages.");
        isPackageBatch = true; packageBatchCancelled = false; NotifyInstallActions();
        var failures = new List<string>(); var installed = 0;
        try
        {
            await RefreshFolderActionsAsync(folder);
            var paths = selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0 || paths.Any(path => !folder.PendingPackagePaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("Select one or more unextracted PKGs from this folder.");
            foreach (var path in paths)
            {
                if (packageBatchCancelled) break;
                var current = Library.Single(item => item.Id == folder.Id);
                try
                {
                    await InstallImageAsync(path, confirmation =>
                    {
                        var stem = ImageFileSystem.FolderName(Path.GetFileNameWithoutExtension(path));
                        if (stem.Length > 100) stem = stem[..100];
                        for (var index = 0; index < 10000; index++)
                        {
                            var name = stem + (index == 0 ? "" : " (" + index + ")");
                            if (!Directory.Exists(Path.Combine(current.ContentPath, name)) && !File.Exists(Path.Combine(current.ContentPath, name)))
                                return Task.FromResult<InstallationChoice?>(new(name, !RetainOriginalImage));
                        }
                        throw new IOException("No unused package destination folder is available.");
                    }, packageFolder: current);
                    if (SelectedInstallation is { Registered: true, Verified: true, State: InstallationState.Completed or InstallationState.CompletedOriginalRetained }) installed++;
                    else failures.Add(Path.GetFileName(path) + ": " + SelectedInstallation?.Error);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { failures.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
        }
        finally
        {
            try { await RefreshFolderActionsAsync(Library.FirstOrDefault(item => item.Id == folder.Id)); }
            finally { isPackageBatch = false; NotifyInstallActions(); }
        }
        Status = $"Installed {installed} package(s)." + (packageBatchCancelled ? " Remaining packages were cancelled." : "") +
            (failures.Count == 0 ? "" : " Still pending: " + string.Join("; ", failures));
    }
}
