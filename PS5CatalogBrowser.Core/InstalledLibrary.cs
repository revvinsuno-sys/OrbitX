namespace PS5CatalogBrowser.Core;

public static class InstalledLibrary
{
    // Old versions admitted library containers and individual files inside extracted folders.
    // Retire only unowned metadata; installation, recovery and file deletion remain separate.
    public static int RetireObsoleteReferences(AppSettings settings, Action<AppSettings> persist)
    {
        var roots = new[] { settings.DownloadRoot, settings.InstalledGamesRoot }.Where(Path.IsPathFullyQualified).ToArray();
        var obsolete = settings.Library.Where(item =>
        {
            if (item.InstalledGame is not null || item.ContentMove is not null || item.DeletionTargets is not null ||
                item.PackageInstallations.Count > 0 || item.AdditionalPaths.Count > 0) return false;
            if (item.Source == "Local" && item.DownloadRegistrationId.Length == 0 && item.DownloadArtifacts.Count == 0 && item.DownloadPath.Length == 0)
                return Path.IsPathFullyQualified(item.ContentPath) && roots.Any(root => Same(item.ContentPath, root) || ImageFileSystem.IsWithin(item.ContentPath, root));
            if (item.DownloadRegistrationId.Length == 0 || item.DownloadArtifacts.Count == 0 || !Path.IsPathFullyQualified(settings.DownloadRoot)) return false;
            var jobs = settings.Downloads.Where(job => job.Id == item.DownloadRegistrationId || item.DownloadArtifacts.Any(a => a.JobId == job.Id)).ToArray();
            if (jobs.Length == 0 || !jobs.Any(job => job.Id == item.DownloadRegistrationId) ||
                item.DownloadArtifacts.Any(a => !jobs.Any(job => job.Id == a.JobId)) ||
                jobs.Any(job => job.Method != "Scan" || !job.IsCleared || DownloadCompletion.Unresolved(job) || job.CleanupPending || HttpResume.Reserves(job) ||
                    job.State is not ("Imported" or "Downloaded" or "Missing" or "Changed" or "Installed"))) return false;
            var paths = new[] { item.ContentPath, item.DownloadPath }.Concat(item.DownloadArtifacts.Select(a => a.Destination)).Where(path => path.Length > 0).ToArray();
            if (paths.Any(path => !Path.IsPathFullyQualified(path) || !ImageFileSystem.IsWithin(settings.DownloadRoot, path))) return false;
            return paths.All(path => !File.Exists(path) && !Directory.Exists(path)) ||
                paths.All(path => ImageFileSystem.IsWithin(settings.DownloadRoot, path) &&
                    (!Same(Path.GetDirectoryName(path)!, settings.DownloadRoot) || !DownloadCompletion.SupportedScanFile(path)));
        }).ToArray();
        if (obsolete.Length == 0) return 0;
        var candidate = SettingsStore.Snapshot(settings);
        candidate.Library.RemoveAll(item => obsolete.Any(old => old.Id == item.Id));
        persist(candidate);
        foreach (var item in obsolete) settings.Library.Remove(item);
        return obsolete.Length;
    }

    public static LocalLibraryEntry Register(AppSettings settings, InstallationJob job)
    {
        if (!job.Verified || !Directory.Exists(job.Destination)) throw new InvalidOperationException("Installation is not verified and finalised.");
        if (job.ParentLibraryId.Length > 0)
        {
            var parent = settings.Library.Single(item => item.Id == job.ParentLibraryId);
            if (!ImageFileSystem.SamePath(parent.ContentPath, job.InstallationRoot) ||
                !ImageFileSystem.IsWithin(parent.ContentPath, job.SourcePath) || !ImageFileSystem.IsWithin(parent.ContentPath, job.Destination) || parent.DeletionTargets is not null)
                throw new IOException("Package no longer belongs to the selected library folder.");
            if (settings.Library.Any(other => other != parent && new[] { other.ContentPath, other.DownloadPath }.Concat(other.AdditionalPaths)
                .Any(path => Same(path, job.SourcePath)))) throw new IOException("Another library entry references this package; source preserved.");
            parent.PackageInstallations.RemoveAll(package => package.InstallationId == job.Id);
            parent.PackageInstallations.Add(new(job.Id, job.SourcePath, job.SourceSha256, job.SourceSize, job.Destination,
                Path.Combine(job.Destination, job.GameRootRelative)) { SourceDeleted = job.SourceDeleted });
            return parent;
        }
        if (settings.Library.Any(item => item.AdditionalPaths.Any(part => Same(part, job.SourcePath))))
            throw new InvalidOperationException("Source is referenced by a multipart library item. Resolve that item before installation.");
        var matches = settings.Library.Where(item => Same(item.ContentPath, job.SourcePath) || Same(item.DownloadPath, job.SourcePath)).ToArray();
        if (matches.Length == 0) { matches = [new LocalLibraryEntry()]; settings.Library.Add(matches[0]); }
        foreach (var item in matches)
        {
            if (item.AdditionalPaths.Count > 0) throw new InvalidOperationException("Multipart library items cannot be installed as one image.");
            item.CatalogEntryId = job.CatalogEntryId; item.Title = job.Title; item.Source = job.CatalogSource;
            item.ContentPath = Path.Combine(job.Destination, job.GameRootRelative); item.DownloadPath = "";
            item.Integrity = "All extracted files SHA-256 verified"; item.Extraction = "Installed";
            item.Classification = "Installed — Ready for launch attempt";
            item.InstalledGame = new()
            {
                InstallationId = job.Id, TitleId = job.TitleId, OriginalImageFilename = Path.GetFileName(job.SourcePath),
                Directory = job.Destination, FileCount = job.Files.Count, Size = job.TotalBytes, InstalledAt = job.InstalledAt,
                Verification = item.Integrity, LaunchEligible = true
            };
        }
        return matches[0];
    }
    public static bool IsRegistered(AppSettings settings, InstallationJob job) => job.ParentLibraryId.Length > 0
        ? settings.Library.Any(item => item.Id == job.ParentLibraryId && ImageFileSystem.SamePath(item.ContentPath, job.InstallationRoot) &&
            item.PackageInstallations.Any(package => package.InstallationId == job.Id && Same(package.SourcePath, job.SourcePath) &&
                package.SourceSha256 == job.SourceSha256 && package.SourceSize == job.SourceSize && Same(package.Directory, job.Destination) &&
                Same(package.ContentPath, Path.Combine(job.Destination, job.GameRootRelative))))
        : settings.Library.Any(item =>
        item.InstalledGame is { } game && game.InstallationId == job.Id && Same(game.Directory, job.Destination) &&
        Same(item.ContentPath, Path.Combine(job.Destination, job.GameRootRelative)) && game.FileCount == job.Files.Count && game.Size == job.TotalBytes &&
        game.Verification == "All extracted files SHA-256 verified" && game.LaunchEligible && item.DownloadPath.Length == 0 &&
        item.Title == job.Title && item.Source == job.CatalogSource && item.CatalogEntryId == job.CatalogEntryId &&
        game.TitleId == job.TitleId && game.OriginalImageFilename == Path.GetFileName(job.SourcePath) && game.InstalledAt == job.InstalledAt);
    public static void UpdateCleanup(AppSettings settings, InstallationJob job)
    {
        foreach (var package in settings.Library.SelectMany(item => item.PackageInstallations).Where(package => package.InstallationId == job.Id))
        {
            package.SourceDeleted = job.SourceDeleted;
            package.LaunchEligible = job.State is InstallationState.Completed or InstallationState.CompletedOriginalRetained;
        }
        foreach (var item in settings.Library.Where(item => item.InstalledGame?.InstallationId == job.Id))
            item.InstalledGame!.OriginalImageDeletion = job.CleanupResult;
        if (!job.Verified || !job.Registered || job.State is not (InstallationState.Completed or InstallationState.CompletedOriginalRetained)) return;
        foreach (var download in settings.Downloads.Where(download => Same(download.Destination, job.SourcePath)))
        {
            download.State = job.SourceDeleted ? "Installed — original removed" : "Installed — original retained";
            download.Error = job.SourceDeleted ? "Original image removed after verified installation to " + job.Destination : "Original retained after verified installation to " + job.Destination;
            download.IsCleared = job.SourceDeleted;
        }
    }
    private static bool Same(string a, string b) => ImageFileSystem.SamePath(a, b);
}
