namespace PS5CatalogBrowser.Core;

public sealed class ImageInstallationCoordinator(IImageMounter mounter, InstallationJournal journal,
    Func<InstallationJob, Task> register, Func<InstallationJob, Task<bool>> registrationExists,
    Func<string, bool> sourceInUse, Func<string, long>? freeSpace = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task InstallAsync(InstallationJob job, Func<InstallationConfirmation, Task<InstallationChoice?>> confirm,
        IProgress<InstallationProgress>? progress, CancellationToken cancellation)
    {
        if (!await gate.WaitAsync(0, cancellation)) throw new InvalidOperationException("Another image installation is active.");
        IImageMount? mount = null; InstallationSource? source = null; FileStream? ownership = null;
        try
        {
            Stage(InstallationState.CheckingPrerequisites);
            await mounter.CheckAsync(cancellation);
            job.SourcePath = Path.GetFullPath(job.SourcePath); job.InstallationRoot = Path.GetFullPath(job.InstallationRoot);
            if (job.InstallationRoot.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(job.InstallationRoot)!).DriveType != DriveType.Fixed)
                throw new IOException("Installed games require a local fixed drive with an atomic directory rename.");
            if (sourceInUse(job.SourcePath)) throw new IOException("Image belongs to an active or incomplete download or another operation.");
            if (!InstallationSource.IsCandidate(job.SourcePath)) throw new InvalidDataException("Select a complete exFAT image or PS4/PS5 PKG with a supported signature.");
            source = new(job.SourcePath);
            if (!InstallationSource.HasSignature(job.SourcePath, source.Stream)) throw new InvalidDataException("Source changed before ownership could be acquired.");
            job.SourceIdentity = source.Identity; job.SourceSize = source.Stream.Length;
            job.SourceSha256 = await HashSourceAsync(source.Stream, InstallationState.CheckingPrerequisites, progress, cancellation);
            ImageFileSystem.EnsureNoLinks(job.InstallationRoot);
            Directory.CreateDirectory(job.InstallationRoot);
            // An exclusive cross-process lock protects the root across catalogue browser instances.
            ownership = new FileStream(Path.Combine(job.InstallationRoot, ".installation-lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            Stage(InstallationState.Mounting);
            mount = await mounter.MountAsync(job.SourcePath, cancellation);
            job.MountDescription = mount.Description; journal.Save(job);
            Stage(InstallationState.Scanning);
            var scan = await ImageFileSystem.ScanAsync(mount.Root, cancellation, job.RequireGameRoot);
            if (scan.Files.Count == 0) throw new InvalidDataException("Package contains no files to install.");
            job.Title = scan.Title.Length > 0 ? scan.Title : job.Title.Length > 0 ? job.Title : Path.GetFileNameWithoutExtension(job.SourcePath);
            if (scan.TitleId.Length > 0) job.TitleId = scan.TitleId;
            job.Files = scan.Files; job.Directories = scan.Directories; job.GameRootRelative = scan.GameRootRelative;
            var proposed = ImageFileSystem.FolderName(job.Title);
            var proposedPath = Path.Combine(job.InstallationRoot, proposed);
            if (Directory.Exists(proposedPath) || File.Exists(proposedPath))
                proposed = ImageFileSystem.FolderName(job.Title + (job.TitleId.Length > 0 ? " (" + job.TitleId + ")" : " (new installation)"));
            var available = Available(job.InstallationRoot);
            CheckSpace(scan.TotalBytes, available);
            var choice = await confirm(new(job.Title, proposed, job.SourcePath, Path.Combine(job.InstallationRoot, proposed), job.SourceSize, scan.TotalBytes, available, job.DeleteOriginal));
            if (choice is null) throw new OperationCanceledException("Installation confirmation cancelled.", cancellation);
            cancellation.ThrowIfCancellationRequested();
            job.DeleteOriginal = choice.DeleteOriginal;
            job.Destination = ImageFileSystem.Destination(job.InstallationRoot, choice.FolderName);
            CheckSpace(scan.TotalBytes, Available(job.InstallationRoot));
            job.StagingPath = Path.Combine(job.InstallationRoot, ".install-" + job.Id);
            if (Directory.Exists(job.StagingPath) || File.Exists(job.StagingPath)) throw new IOException("Staging directory already exists.");
            journal.Save(job);
            Directory.CreateDirectory(job.StagingPath);
            File.WriteAllText(Path.Combine(job.StagingPath, "owner"), job.Id);
            var content = Path.Combine(job.StagingPath, "content"); Directory.CreateDirectory(content);
            Stage(InstallationState.Extracting);
            await ImageFileSystem.CopyAsync(mount.Root, content, scan, progress, cancellation);
            Stage(InstallationState.Verifying);
            job.Files = await ImageFileSystem.VerifyAsync(mount.Root, content, scan, progress, cancellation);
            job.Verified = true; journal.Save(job);
            await mount.ReleaseAsync(); await mount.DisposeAsync(); mount = null;
            job.MountDescription = "Released";
            cancellation.ThrowIfCancellationRequested();
            Stage(InstallationState.Finalising);
            ImageFileSystem.Destination(job.InstallationRoot, choice.FolderName);
            // Both paths are siblings on the same filesystem. Directory.Move cannot merge or overwrite.
            Directory.Move(content, job.Destination);
            await ImageFileSystem.VerifyFinalAsync(job.Destination, job, cancellation, progress);
            CleanStaging(job);
            job.InstalledAt = DateTimeOffset.Now;
            Stage(InstallationState.RegisteringInLibrary);
            await register(job);
            if (!await registrationExists(job)) throw new IOException("Library registration could not be verified from persisted state.");
            job.Registered = true; journal.Save(job);
            source.Dispose(); source = null;
            if (job.DeleteOriginal)
            {
                cancellation.ThrowIfCancellationRequested();
                Stage(InstallationState.RemovingOriginal);
                await DeleteSourceAsync(job, cancellation, progress);
            }
            else job.CleanupResult = "Original retained by choice";
            Stage(job.SourceDeleted ? InstallationState.Completed : InstallationState.CompletedOriginalRetained);
        }
        catch (OperationCanceledException ex)
        {
            job.State = job.Destination.Length > 0 && Directory.Exists(job.Destination) ? InstallationState.NeedsAttention : InstallationState.Cancelled;
            job.Error = ex.Message;
        }
        catch (Exception ex)
        {
            job.State = ex.Message.Contains("Mount cleanup needs attention", StringComparison.Ordinal) ? InstallationState.NeedsAttention : job.Registered ? InstallationState.CompletedOriginalRetained :
                job.Destination.Length > 0 && Directory.Exists(job.Destination) ? InstallationState.NeedsAttention : InstallationState.Failed;
            job.Error = ex.Message;
            if (job.Registered) job.CleanupResult = "Installation succeeded; original cleanup failed: " + ex.Message;
        }
        finally
        {
            if (mount is not null)
            {
                try { await mount.ReleaseAsync(); job.MountDescription = "Released"; }
                catch (Exception ex) { job.State = InstallationState.NeedsAttention; job.Error += " Mount release failed: " + ex.Message; }
                finally { await mount.DisposeAsync(); }
            }
            source?.Dispose();
            try { CleanStaging(job); }
            catch (Exception ex) { job.State = InstallationState.NeedsAttention; job.Error += " Staging retained: " + ex.Message; }
            ownership?.Dispose(); gate.Release();
            journal.Save(job);
            progress?.Report(new(job.State, job.Error, job.SourceDeleted || job.State == InstallationState.CompletedOriginalRetained ? job.TotalBytes : 0, job.TotalBytes, 0, TimeSpan.Zero));
        }
        void Stage(InstallationState state) { job.State = state; journal.Save(job); progress?.Report(new(state, "", 0, job.TotalBytes, 0, TimeSpan.Zero)); }
    }
    private long Available(string path) => freeSpace?.Invoke(path) ?? new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
    private static void CheckSpace(long bytes, long available)
    {
        var headroom = Math.Max(64L * 1024 * 1024, bytes / 20);
        if (available < checked(bytes + headroom)) throw new IOException("Insufficient free space for extracted content plus 5% (minimum 64 MiB) working space.");
    }
    private async Task DeleteSourceAsync(InstallationJob job, CancellationToken cancellation, IProgress<InstallationProgress>? progress = null)
    {
        if (!job.Verified || !job.Registered || !job.DeleteOriginal || job.MountDescription != "Released" || sourceInUse(job.SourcePath) || !await registrationExists(job))
            throw new IOException("Source cleanup prerequisites are incomplete. Original retained.");
        if (!InstallationSource.IsCandidate(job.SourcePath)) throw new IOException("Recorded source is no longer a supported image or package. Original retained.");
        using (var exactSource = new InstallationSource(job.SourcePath, forDeletion: true))
        {
            if (exactSource.Identity != job.SourceIdentity || exactSource.Stream.Length != job.SourceSize ||
                await HashSourceAsync(exactSource.Stream, InstallationState.RemovingOriginal, progress, cancellation) != job.SourceSha256)
                throw new IOException("Source image identity or contents changed. Original retained.");
            await ImageFileSystem.VerifyFinalAsync(job.Destination, job, cancellation, progress);
            cancellation.ThrowIfCancellationRequested();
            if (sourceInUse(job.SourcePath) || !await registrationExists(job)) throw new IOException("Source ownership or persisted registration changed. Original retained.");
            exactSource.Delete();
        }
        job.SourceDeleted = true;
        job.CleanupResult = File.Exists(job.SourcePath) ? "Original image deleted; a different file now occupies the source path and has been preserved" :
            "Original image deleted after verification and persisted registration";
    }
    private static async Task<string> HashSourceAsync(Stream stream, InstallationState state, IProgress<InstallationProgress>? progress, CancellationToken cancellation)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew(); long bytes = 0; long reported = -250;
        return await ImageFileSystem.HashAsync(stream, cancellation, read =>
        {
            bytes += read;
            if (clock.ElapsedMilliseconds - reported < 250) return;
            reported = clock.ElapsedMilliseconds;
            progress?.Report(new(state, "Verifying original image identity and full contents", bytes, stream.Length,
                bytes / Math.Max(.001, clock.Elapsed.TotalSeconds), clock.Elapsed));
        });
    }
    public async Task RetryCleanupAsync(InstallationJob job, CancellationToken cancellation, IProgress<InstallationProgress>? progress = null)
    {
        if (!job.CanRetryCleanup) throw new InvalidOperationException("This installation is not eligible for cleanup.");
        if (!await gate.WaitAsync(0, cancellation)) throw new InvalidOperationException("Another installation is active.");
        try
        {
            // Explicit retry reconciles final files, persisted registration and exact source identity again.
            await mounter.CheckAsync(cancellation);
            job.State = InstallationState.RemovingOriginal; journal.Save(job);
            await DeleteSourceAsync(job, cancellation, progress);
            job.State = InstallationState.Completed; job.Error = "";
        }
        catch (Exception ex) { job.State = InstallationState.NeedsAttention; job.Error = ex.Message; throw; }
        finally
        {
            journal.Save(job); gate.Release();
            progress?.Report(new(job.State, job.Error, job.SourceDeleted ? job.TotalBytes : 0, job.TotalBytes, 0, TimeSpan.Zero));
        }
    }
    public static void CleanStaging(InstallationJob job)
    {
        if (job.StagingPath.Length == 0 || !Directory.Exists(job.StagingPath)) return;
        var expected = Path.Combine(job.InstallationRoot, ".install-" + job.Id);
        if (!Guid.TryParseExact(job.Id, "N", out _) || !Path.GetFullPath(job.StagingPath).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Staging ownership cannot be established.");
        ImageFileSystem.EnsureNoLinks(job.StagingPath);
        if (File.ReadAllText(Path.Combine(job.StagingPath, "owner")) != job.Id) throw new IOException("Staging ownership marker does not match.");
        // Validate every child first; recursive cleanup is confined to the proven job container.
        var allowed = new HashSet<string>(job.Files.Select(file => Path.Combine("content", file.RelativePath))
            .Concat(job.Directories.Select(path => Path.Combine("content", path))).Concat(["owner", "content"]), StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(job.StagingPath);
        while (pending.TryPop(out var directory))
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                ImageFileSystem.EnsureNoLinks(child);
                if (!allowed.Contains(Path.GetRelativePath(job.StagingPath, child))) throw new IOException("Staging contains an entry not owned by the extraction manifest; it has been preserved.");
                if (Directory.Exists(child)) pending.Push(child);
            }
        Directory.Delete(job.StagingPath, true);
    }
}
