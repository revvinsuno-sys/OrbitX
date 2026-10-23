namespace PS5CatalogBrowser.Core;

public static class InstalledArchiveCleanup
{
    public static bool IsInstalledSource(AppSettings settings, DownloadJob job) => settings.Library.Any(item =>
        item.ContentMove is { Phase: "Completed" } && item.DownloadArtifacts.Any(artifact => artifact.JobId == job.Id) &&
        ImageFileSystem.IsWithin(settings.InstalledGamesRoot, item.ContentPath) && Directory.Exists(item.ContentPath));

    public static void ReconcileAbsentSources(AppSettings settings)
    {
        // Metadata-only recovery after deletion or manual removal. Never delete on startup.
        foreach (var job in settings.Downloads.Where(job => !DownloadCompletion.Unresolved(job) &&
            job.State is ("Downloaded" or "Imported" or "Missing" or "Installed") &&
            Path.GetExtension(job.Destination).ToLowerInvariant() is (".zip" or ".rar") && IsInstalledSource(settings, job) &&
            !File.Exists(job.Destination) && !Directory.Exists(job.Destination)))
        { job.State = "Installed"; job.IsCleared = true; }
    }

    public static async Task CompleteAsync(AppSettings settings, LocalLibraryEntry item, SettingsStore store,
        Func<DownloadJob, bool> active, CancellationToken cancellation, IProgress<InstallationProgress>? progress = null)
    {
        if (item.ContentMove is not { Phase: "Completed", Directory: true } move || item.DownloadArtifacts.Count == 0) return;
        if (item.DownloadArtifacts.Count != 1) throw new IOException("Archive cleanup requires one recorded source archive.");
        var artifact = item.DownloadArtifacts[0];
        if (Path.GetExtension(artifact.Destination).ToLowerInvariant() is not (".zip" or ".rar")) return;
        var job = settings.Downloads.SingleOrDefault(job => job.Id == artifact.JobId)
            ?? throw new IOException("The original archive download record is missing; source preserved.");
        if (!IsInstalledSource(settings, job) || DownloadCompletion.Unresolved(job) || job.State is not ("Downloaded" or "Imported" or "Installed") ||
            !ImageFileSystem.SamePath(job.Destination, artifact.Destination) || !ImageFileSystem.IsWithin(settings.DownloadRoot, job.Destination) ||
            ImageFileSystem.IsWithin(settings.InstalledGamesRoot, job.Destination) || artifact.Sha256.Length != 64 ||
            !artifact.Sha256.Equals(job.ActualSha256, StringComparison.OrdinalIgnoreCase) || artifact.Length != job.DownloadedBytes)
            throw new IOException("Archive cleanup evidence is incomplete; source preserved.");
        EnsureUnshared();
        EnsureRegistration();
        cancellation.ThrowIfCancellationRequested();
        if (!settings.RetainOriginalImage && File.Exists(job.Destination))
        {
            using var source = new InstallationSource(job.Destination, forDeletion: true);
            var report = RunnableContentMove.HashProgress(progress, artifact.Length, "Verifying original archive before removal");
            if (source.Stream.Length != artifact.Length ||
                !(await ImageFileSystem.HashAsync(source.Stream, cancellation, bytes => report(Path.GetFileName(job.Destination), bytes))).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source archive changed after download; source preserved.");
            await RunnableContentMove.VerifyAsync(move, item.ContentPath, cancellation, progress);
            EnsureUnshared(); EnsureRegistration(); cancellation.ThrowIfCancellationRequested();
            if (!settings.RetainOriginalImage)
            {
                job.ArchiveStatus = "Removing verified installed source archive";
                store.Save(settings); // Explicit cleanup intent; startup never resumes deletion.
                source.Delete();
            }
        }
        else await RunnableContentMove.VerifyAsync(move, item.ContentPath, cancellation, progress);
        var oldState = job.State; var oldCleared = job.IsCleared; var oldError = job.Error; var oldStatus = job.ArchiveStatus;
        job.State = "Installed"; job.IsCleared = !File.Exists(job.Destination); job.Error = job.ArchiveStatus = "";
        try { store.Save(settings); }
        catch { job.State = oldState; job.IsCleared = oldCleared; job.Error = oldError; job.ArchiveStatus = oldStatus; throw; }

        void EnsureRegistration()
        {
            var saved = store.Load().Library.SingleOrDefault(record => record.Id == item.Id);
            if (!IsInstalledSource(settings, job) || saved?.ContentMove is not { Phase: "Completed" } recorded ||
                !ImageFileSystem.SamePath(saved.ContentPath, item.ContentPath) || !ImageFileSystem.SamePath(recorded.Destination, move.Destination) ||
                !recorded.Files.SequenceEqual(move.Files) || !recorded.Directories.SequenceEqual(move.Directories))
                throw new IOException("The installed folder registration is not durable; source preserved.");
        }

        void EnsureUnshared()
        {
            if (active(job) || settings.Downloads.Any(other => other != job && ImageFileSystem.SamePath(other.Destination, job.Destination) &&
                (!other.IsCleared || other.CleanupPending || DownloadCompletion.Unresolved(other) || active(other))) ||
                settings.Library.Any(other => other != item && new[] { other.ContentPath, other.DownloadPath }.Concat(other.AdditionalPaths)
                    .Concat(other.DownloadArtifacts.Select(a => a.Destination)).Any(path => ImageFileSystem.SamePath(path, job.Destination))))
                throw new IOException("Another operation or library entry owns the source archive; source preserved.");
        }
    }
}
