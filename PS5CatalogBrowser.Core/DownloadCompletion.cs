using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PS5CatalogBrowser.Core;

public static class DownloadCompletion
{
    private static readonly ConditionalWeakTable<DownloadJob, SemaphoreSlim> Owners = new();
    public static bool Unresolved(DownloadJob job) => job.Completion is { Phase: not "Completed" };

    // Identity binds the intent to the admitted snapshot, not a refreshed catalogue or native capability URL.
    private static string Identity(DownloadJob job)
    {
        var original = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            job.Id, job.SelectionIdentity, job.EntryId, job.SourceId, job.Source, job.ArtifactId, job.Url, job.GroupId,
            job.Title, job.Version, job.Method, job.FileName, job.StagingRoot, job.StagingPath, job.StagingOwner,
            job.Destination, job.ExpectedSize, job.ExpectedSha256, job.ActualSha256, job.DownloadedBytes, job.TotalBytes
        })));
        var selected = job.OrbitRelease is null ? original : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { original, job.OrbitRelease })));
        return job.HttpCheckpoint is null ? selected : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { selected, job.HttpCheckpoint })));
    }

    public static async Task<LocalLibraryEntry> CompleteAsync(DownloadJob job, AppSettings settings, Action<AppSettings> persist,
        CancellationToken cancellation, Action<string, DownloadJob>? checkpoint = null)
    {
        var owner = Owners.GetValue(job, _ => new SemaphoreSlim(1, 1));
        await owner.WaitAsync(cancellation);
        try
        {
            if (job.Completion?.Phase == "Completed") return Registered(settings, job);
            if (job.Completion is not null) throw new IOException("This completion needs startup reconciliation. Keep its files and restart the application.");
            if (!settings.Downloads.Contains(job)) throw new InvalidOperationException("Completion requires a retained download record.");
            job.State = "Verifying download"; persist(settings);
            checkpoint?.Invoke("BeforeVerification", job);
            await BrowserDownloadFiles.VerifyAsync(job, cancellation, checkpoint);
            checkpoint?.Invoke("AfterVerification", job);
            cancellation.ThrowIfCancellationRequested();
            job.Completion = new() { Identity = Identity(job), Length = job.DownloadedBytes, Sha256 = job.ActualSha256 };
            job.State = "Finalising download";
            // Failure here must not move the file. The catch retains the verified staging and failure record.
            persist(settings);
            checkpoint?.Invoke("AfterIntent", job);
            var item = await FinaliseAsync(job, settings, persist, checkpoint);
            try { await RunnableContentMove.MoveAsync(settings, item, persist, _ => false, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                job.Error = "Download completed; runnable-content move needs attention: " + ex.Message;
                persist(settings);
            }
            return item;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (job.Completion is not null) Attention(job, settings, persist, ex);
            throw;
        }
        finally { owner.Release(); }
    }

    public static async Task RecoverAsync(DownloadJob job, AppSettings settings, Action<AppSettings> persist,
        Action<string, DownloadJob>? checkpoint = null)
    {
        var owner = Owners.GetValue(job, _ => new SemaphoreSlim(1, 1));
        await owner.WaitAsync();
        try
        {
            if (!Unresolved(job)) return;
            job.State = "Recovering completed download"; job.IsCleared = false;
            await FinaliseAsync(job, settings, persist, checkpoint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Attention(job, settings, persist, ex); }
        finally { owner.Release(); }
    }

    private static async Task<LocalLibraryEntry> FinaliseAsync(DownloadJob job, AppSettings settings, Action<AppSettings> persist,
        Action<string, DownloadJob>? checkpoint)
    {
        var intent = job.Completion!;
        if (settings.Downloads.Count(record => record.Id == job.Id) != 1)
            throw new InvalidDataException("Completion job identity is duplicated. Files were preserved.");
        if (intent.Phase is not ("Pending" or "Promoted") || intent.Identity != Identity(job) ||
            intent.Length < 0 || intent.Length != job.DownloadedBytes || !Digest(intent.Sha256) ||
            !intent.Sha256.Equals(job.ActualSha256, StringComparison.OrdinalIgnoreCase) ||
            job.ExpectedSha256.Length > 0 && !intent.Sha256.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase) ||
            job.ExpectedSize is > 0 && intent.Length != job.ExpectedSize || job.TotalBytes is > 0 && intent.Length != job.TotalBytes || job.CleanupPending)
            throw new InvalidDataException("Saved completion identity or verification evidence is inconsistent. Files were preserved.");
        BrowserDownloadFiles.ValidateCompletionOwnership(job);
        var staging = File.Exists(job.StagingPath); var final = File.Exists(job.Destination);
        if (staging && final) throw new IOException("Both staging and final files exist. Keep both and inspect the destination conflict.");
        if (!staging && !final) throw new IOException("Neither the verified staging nor final file exists. Locate the missing download before retrying.");
        if (File.Exists(job.StagingPath + ".crdownload")) throw new IOException("A native partial remains beside this download. Keep the files for manual attention.");
        if (staging)
        {
            if (intent.Phase == "Promoted") throw new IOException("A promoted download has returned to staging. Files were preserved.");
            using (var stream = OpenVerifiedPath(job.StagingPath))
            {
                VerifyHttpFileIdentity(job, stream);
                await VerifyEvidenceAsync(stream, intent);
            }
            checkpoint?.Invoke("BeforeMove", job);
            BrowserDownloadFiles.ValidateCompletionOwnership(job);
            File.Move(job.StagingPath, job.Destination, false);
            checkpoint?.Invoke("AfterMove", job);
        }
        BrowserDownloadFiles.ValidateCompletionOwnership(job);
        // Keep the final file protected against writes/replacement through registration persistence.
        using var finalStream = OpenVerifiedPath(job.Destination);
        VerifyHttpFileIdentity(job, finalStream);
        await VerifyEvidenceAsync(finalStream, intent);
        if (intent.Phase != "Promoted")
        {
            var previous = intent.Phase;
            intent.Phase = "Promoted";
            try { persist(settings); }
            catch { intent.Phase = previous; throw; }
            checkpoint?.Invoke("AfterPromoted", job);
        }
        var candidate = SettingsStore.Snapshot(settings);
        var candidateJob = candidate.Downloads.Single(record => record.Id == job.Id);
        candidateJob.Completion!.Phase = "Completed";
        candidateJob.State = job.Method == "External import" ? "Imported" : "Downloaded";
        candidateJob.Error = "";
        var item = Register(candidate, candidateJob);
        checkpoint?.Invoke("BeforeRegistrationSave", job);
        persist(candidate); // Library and Completed are one atomic settings replacement.
        intent.Phase = "Completed"; job.State = candidateJob.State; job.Error = "";
        var index = settings.Library.FindIndex(existing => existing.Id == item.Id);
        if (index < 0) settings.Library.Add(item); else settings.Library[index] = item;
        checkpoint?.Invoke("AfterCompleted", job);
        return item;
    }

    private static FileStream OpenVerifiedPath(string path)
    {
        ImageFileSystem.EnsureNoLinks(path);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Download file is a filesystem link.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void VerifyHttpFileIdentity(DownloadJob job, FileStream stream)
    {
        if (job.HttpCheckpoint is { } state && (state.Identity != HttpResume.Identity(job, state) ||
            state.FileIdentity != HttpResume.FileIdentity(stream) || state.Cursor != job.Completion!.Length || state.PrefixSha256 != job.Completion.Sha256))
            throw new InvalidDataException("HTTP completion checkpoint or file identity changed. Files were preserved.");
    }

    private static async Task VerifyEvidenceAsync(FileStream stream, DownloadCompletionIntent intent)
    {
        if (stream.Length != intent.Length) throw new InvalidDataException("Verified download length changed. Files were preserved.");
        var header = new byte[256]; var count = await stream.ReadAsync(header);
        var text = System.Text.Encoding.UTF8.GetString(header, 0, count).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download contains an HTML page. Files were preserved.");
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!hash.Equals(intent.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Verified download SHA-256 changed. Files were preserved.");
    }

    private static string RegistrationId(DownloadJob job) => job.GroupId.Length > 0 ? job.GroupId : job.Id;
    // Inventory every top-level file; recognition and install eligibility are separate checks.
    public static bool SupportedScanFile(string path) => !Path.GetFileName(path).StartsWith('.');
    public static int RetireUnsupportedScanHistory(AppSettings settings, Action<AppSettings> persist)
    {
        var obsolete = settings.Downloads.Where(job => job.Method == "Scan" && !job.IsCleared && !Unresolved(job) && !job.CleanupPending &&
            job.State is ("Imported" or "Downloaded" or "Missing" or "Changed") && ImageFileSystem.IsWithin(settings.DownloadRoot, job.Destination) &&
            (!ImageFileSystem.SamePath(Path.GetDirectoryName(job.Destination)!, settings.DownloadRoot) || !SupportedScanFile(job.Destination))).ToArray();
        if (obsolete.Length == 0) return 0;
        var candidate = SettingsStore.Snapshot(settings);
        foreach (var job in candidate.Downloads.Where(job => obsolete.Any(old => old.Id == job.Id))) job.IsCleared = true;
        persist(candidate);
        foreach (var job in obsolete) job.IsCleared = true;
        return obsolete.Length;
    }
    // Scanning registers an existing file in place; it never promotes, copies or deletes it.
    public static LocalLibraryEntry RegisterScanned(AppSettings settings, DownloadJob job, Action<AppSettings> persist)
    {
        if (job.Method != "Scan" || job.ActualSha256.Length is not (0 or 64) || job.DownloadedBytes < 0 || settings.Downloads.Any(existing => existing.Id == job.Id))
            throw new InvalidDataException("Invalid scanned download registration.");
        if (!Path.IsPathFullyQualified(job.Destination) || !ImageFileSystem.SamePath(Path.GetDirectoryName(job.Destination)!, settings.DownloadRoot) || !SupportedScanFile(job.Destination))
            throw new InvalidDataException("Scan accepts supported files directly inside Downloads only.");
        ImageFileSystem.EnsureNoLinks(job.Destination);
        if (new FileInfo(job.Destination).Length != job.DownloadedBytes) throw new InvalidDataException("Scanned file changed before registration.");
        var candidate = SettingsStore.Snapshot(settings);
        job.Completion = new() { Phase = "Completed", Length = job.DownloadedBytes, Sha256 = job.ActualSha256, Identity = Identity(job) };
        job.State = "Imported";
        candidate.Downloads.Add(job);
        var item = Register(candidate, job, inspect: false);
        if (job.ActualSha256.Length == 0) item.Integrity = "Local file; full verification runs during installation";
        else if (job.ExpectedSha256.Length == 0) item.Integrity = "Scanned file; no expected checksum — completeness unverified";
        persist(candidate);
        settings.Downloads.Add(job); settings.Library.Add(item);
        return item;
    }

    // The caller owns a read-only source lease through extraction. Inventory itself never hashes content.
    public static async Task VerifyScannedArchiveAsync(AppSettings settings, DownloadJob job, Stream source,
        Action<AppSettings> persist, CancellationToken cancellation, Action<long>? progress = null)
    {
        if (job.Method != "Scan" || job.ActualSha256.Length != 0) return;
        if (source.Length != job.DownloadedBytes) throw new IOException("The inventoried source size changed before installation. Original preserved.");
        source.Position = 0;
        long verifiedBytes = 0;
        var hash = await ImageFileSystem.HashAsync(source, cancellation, bytes => progress?.Invoke(verifiedBytes += bytes));
        if (job.ExpectedSha256.Length > 0 && !hash.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source SHA-256 did not match the expected archive. Original preserved.");
        var candidate = SettingsStore.Snapshot(settings);
        Apply(candidate, candidate.Downloads.Single(record => record.Id == job.Id));
        persist(candidate);
        Apply(settings, job);

        void Apply(AppSettings target, DownloadJob record)
        {
            record.ActualSha256 = hash;
            record.Completion!.Sha256 = hash;
            record.Completion.Identity = Identity(record);
            foreach (var item in target.Library.Where(item => item.DownloadArtifacts.Any(artifact => artifact.JobId == job.Id)))
            {
                foreach (var artifact in item.DownloadArtifacts.Where(artifact => artifact.JobId == job.Id)) artifact.Sha256 = hash;
                item.Integrity = "Local archive SHA-256 recorded before extraction";
            }
        }
    }

    // Replace only old per-file scan registrations, never their files or native/HTTP history.
    // Inventory the folder as one download; installation is a separate explicit action.
    public static LocalLibraryEntry RegisterScannedFolder(AppSettings settings, string folder, Action<AppSettings> persist)
    {
        folder = Path.GetFullPath(folder);
        if (!ImageFileSystem.SamePath(Path.GetDirectoryName(folder)!, settings.DownloadRoot) || Path.GetFileName(folder).StartsWith('.') || !Directory.Exists(folder))
            throw new InvalidDataException("Scan accepts top-level extracted folders only.");
        ImageFileSystem.EnsureNoLinks(folder);
        var scans = settings.Downloads.Where(job => job.Method == "Scan" && ImageFileSystem.IsWithin(folder, job.Destination)).ToArray();
        if (scans.Any(job => Unresolved(job) || job.CleanupPending || !job.IsCleared && job.State is not ("Imported" or "Downloaded" or "Installed")))
            throw new IOException("An incomplete scan registration still owns files inside this folder.");
        var ids = scans.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
        var obsolete = settings.Library.Where(item => ids.Contains(item.DownloadRegistrationId) && item.DownloadArtifacts.All(a => ids.Contains(a.JobId)) &&
            item.ContentMove is null && item.InstalledGame is null && item.DeletionTargets is null && item.AdditionalPaths.Count == 0 &&
            ImageFileSystem.IsWithin(folder, item.ContentPath) && ImageFileSystem.IsWithin(folder, item.DownloadPath)).ToArray();
        var matches = settings.Library.Except(obsolete).Where(item => ImageFileSystem.SamePath(item.ContentPath, folder)).ToArray();
        if (matches.Length > 1) throw new IOException("Multiple library records own this folder.");
        var existing = matches.SingleOrDefault();
        var item = existing ?? new LocalLibraryEntry { Title = Path.GetFileName(folder), ContentPath = folder };
        var candidate = SettingsStore.Snapshot(settings);
        candidate.Library.RemoveAll(record => obsolete.Any(old => old.Id == record.Id));
        var target = candidate.Library.SingleOrDefault(record => record.Id == item.Id);
        if (target is null) { target = item; candidate.Library.Add(target); }
        target.Extraction = "Extracted";
        var folderJob = candidate.Downloads.FirstOrDefault(job => ImageFileSystem.SamePath(job.Destination, folder));
        if (folderJob is null)
        {
            folderJob = new DownloadJob { Method = "Folder scan", State = "Imported", Destination = folder,
                Title = item.Title, FileName = Path.GetFileName(folder), Source = "Scanned Downloads" };
            candidate.Downloads.Add(folderJob);
        }
        folderJob.IsCleared = false;
        target.DownloadRegistrationId = folderJob.Id;
        target.DownloadPath = folder;
        foreach (var record in candidate.Downloads.Where(record => ids.Contains(record.Id))) record.IsCleared = true;
        persist(candidate);
        settings.Library.RemoveAll(record => obsolete.Contains(record));
        if (existing is null) settings.Library.Add(item);
        item.Extraction = "Extracted";
        item.DownloadRegistrationId = folderJob.Id; item.DownloadPath = folder;
        var currentJob = settings.Downloads.FirstOrDefault(job => job.Id == folderJob.Id);
        if (currentJob is null) settings.Downloads.Add(folderJob); else currentJob.IsCleared = false;
        foreach (var record in scans) record.IsCleared = true;
        return item;
    }
    private static LocalLibraryEntry Registered(AppSettings settings, DownloadJob job) => settings.Library.Single(item =>
        item.DownloadRegistrationId == RegistrationId(job) && item.DownloadArtifacts.Any(artifact => artifact.JobId == job.Id));

    private static LocalLibraryEntry Register(AppSettings settings, DownloadJob job, bool inspect = true)
    {
        var registration = RegistrationId(job);
        var matches = settings.Library.Where(item => item.DownloadRegistrationId == registration).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Duplicate download library registrations need manual attention.");
        var item = matches.SingleOrDefault();
        if (item is not null && (item.InstalledGame is not null || item.CatalogEntryId != job.EntryId || item.Source != job.Source))
            throw new InvalidDataException("Download registration conflicts with existing library ownership.");
        if (settings.Library.Any(existing => existing != item && new[] { existing.DownloadPath, existing.ContentPath }.Concat(existing.AdditionalPaths)
            .Any(path => ImageFileSystem.SamePath(path, job.Destination))))
            throw new InvalidDataException("The final download is already owned by another library entry.");
        if (item is null)
        {
            item = new() { DownloadRegistrationId = registration, CatalogEntryId = job.EntryId, Title = job.Title,
                SourceId = job.SourceId, CoverUrl = job.CoverUrl, CoverFallbackUrl = job.CoverFallbackUrl,
                Source = job.Source, Version = job.Version, Extraction = "Not extracted" };
            settings.Library.Add(item);
        }
        if (item.DownloadArtifacts.Any(artifact => artifact.JobId == job.Id))
            throw new InvalidDataException("An unresolved download already has a library registration. Files were preserved.");
        item.DownloadArtifacts.Add(new() { JobId = job.Id, SelectionIdentity = job.SelectionIdentity, ArtifactId = job.ArtifactId,
            OrbitRelease = job.OrbitRelease,
            SourceId = job.SourceId, FileName = job.FileName, Destination = job.Destination, Length = job.Completion!.Length,
            Sha256 = job.Completion.Sha256, ExpectedSha256 = job.ExpectedSha256 });
        var group = job.GroupId.Length > 0 ? settings.Downloads.Where(record => record.GroupId == job.GroupId).ToArray() : [job];
        if (group.Any(record => record.EntryId != job.EntryId || record.SourceId != job.SourceId)) throw new InvalidDataException("Batch catalogue identity changed.");
        item.DownloadArtifacts = item.DownloadArtifacts.OrderBy(artifact => Array.FindIndex(group, record => record.Id == artifact.JobId)).ToList();
        item.DownloadPath = item.ContentPath = item.DownloadArtifacts[0].Destination;
        item.AdditionalPaths = item.DownloadArtifacts.Skip(1).Select(artifact => artifact.Destination).ToList();
        item.Integrity = job.Method == "External import" && job.ExpectedSha256.Length == 0 ? "Imported file; no expected checksum — transfer completeness unverified" :
            item.DownloadArtifacts.All(artifact => artifact.ExpectedSha256.Length > 0) ? (group.Length > 1 ? "All SHA-256 checks verified at download" : "SHA-256 verified at download") :
            group.Length > 1 ? "Some files lack expected checksums" : "No expected checksum";
        item.Classification = group.Length == 1 ? inspect ? FileInspection.Classify(job.Destination) : "Local file — not inspected" :
            item.DownloadArtifacts.Count == group.Length ? "Multiple files — assembly unsupported" : "Multiple files — download incomplete";
        return item;
    }

    private static bool Digest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void Attention(DownloadJob job, AppSettings settings, Action<AppSettings> persist, Exception failure)
    {
        job.State = "Needs attention"; job.IsCleared = false; job.Error = failure.Message;
        try { persist(settings); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { job.Error += " The recovery record could not be saved; the last durable record and files were preserved."; }
    }
}
