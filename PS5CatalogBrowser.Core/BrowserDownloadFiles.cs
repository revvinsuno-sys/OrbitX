using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public static class BrowserDownloadFiles
{
    public static string SafeName(string value)
    {
        var name = Path.GetFileName(value);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name is "." or "..") name = "download.bin";
        if (Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) name = "_" + name;
        if (name.Length > 160) name = name[..140] + Path.GetExtension(name)[..Math.Min(20, Path.GetExtension(name).Length)];
        return name;
    }

    public static DownloadJob Prepare(CatalogEntry entry, DownloadMirror mirror, string root, string suggestedName, IEnumerable<DownloadJob> existing)
    {
        OrbitDownloadPolicy.ValidateFileName(entry, mirror.Artifact, suggestedName);
        root = Path.GetFullPath(root);
        NoReparsePoints(root);
        Directory.CreateDirectory(root);
        NoReparsePoints(root);
        var name = SafeName(suggestedName);
        var reserved = existing.Select(x => x.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destination = "";
        for (var n = 0; n < 10000; n++)
        {
            var candidate = Path.Combine(root, n == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !reserved.Contains(candidate)) { destination = candidate; break; }
        }
        if (destination.Length == 0) throw new IOException("No available destination filename.");
        if (mirror.Artifact.Size is > 0 && new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < mirror.Artifact.Size)
            throw new IOException("Insufficient disk space for download.");
        var job = new DownloadJob { EntryId = entry.Id, Title = entry.Title, Source = entry.SourceName, SourceId = entry.SourceId, ArtifactId = mirror.Artifact.Id,
            CoverUrl = entry.CoverUrl, CoverFallbackUrl = entry.CoverFallbackUrl,
            Url = entry.CatalogFormat == "Orbit" ? OrbitDownloadPolicy.BrowserUrl(mirror.Artifact) : mirror.Url,
            OrbitRelease = OrbitReleaseSelection.Capture(entry, mirror.Artifact),
            ExpectedFileName = mirror.ExpectedFileName,
            Mirror = entry.CatalogFormat == "Orbit" ? mirror.Artifact.Provider : mirror.Provider, Method = "Browser", Destination = destination, FileName = Path.GetFileName(destination),
            Version = mirror.Artifact.Version.Length > 0 ? mirror.Artifact.Version : entry.Version,
            ExpectedSize = mirror.Artifact.Size, ExpectedSha256 = mirror.Artifact.Sha256, State = "Downloading" };
        PrepareStaging(job);
        return job;
    }

    public static void PrepareStaging(DownloadJob job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _)) throw new IOException("Invalid staging owner.");
        var name = Path.GetFileName(job.Destination);
        if (name != SafeName(name) || name.StartsWith('.')) throw new IOException("Invalid final download filename.");
        var root = Path.GetDirectoryName(Path.GetFullPath(job.Destination))!;
        var staging = Path.Combine(root, ".ps5catalog-staging", job.Id);
        NoReparsePoints(staging);
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("Staging owner directory already exists.");
        Directory.CreateDirectory(staging);
        NoReparsePoints(staging);
        job.StagingPath = Path.Combine(staging, "payload.part");
        job.StagingRoot = root;
        job.StagingOwner = Guid.NewGuid().ToString("N");
        using var marker = new FileStream(Path.Combine(staging, ".owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(job.Id + ":" + job.StagingOwner + "\n" + Reservation(job));
        marker.Write(bytes); marker.Flush(true);
    }

    public static async Task VerifyAsync(DownloadJob job, CancellationToken token, Action<string, DownloadJob>? checkpoint = null)
    {
        ValidatePaths(job);
        ValidateOwnership(job);
        if (!File.Exists(job.StagingPath)) throw new IOException("Transfer reported completion, but the staged file is missing.");
        var length = new FileInfo(job.StagingPath).Length;
        if (length != job.DownloadedBytes || job.TotalBytes is > 0 && length != job.TotalBytes || job.ExpectedSize is > 0 && length != job.ExpectedSize)
            throw new InvalidDataException("Downloaded file size does not match the browser transfer or catalogue metadata.");
        await using (var stream = new FileStream(job.StagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, true))
        {
            if (job.HttpCheckpoint is { } http)
            {
                HttpResume.ValidateState(job);
                if (HttpResume.FileIdentity(stream) != http.FileIdentity || http.Cursor != length || http.PrefixSha256 != job.ActualSha256)
                    throw new InvalidDataException("HTTP completion no longer matches its durable file identity and digest.");
            }
            stream.Flush(true);
            var header = new byte[256]; var count = await stream.ReadAsync(header, token);
            var text = System.Text.Encoding.UTF8.GetString(header, 0, count).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The transfer downloaded an HTML page instead of content.");
            stream.Position = 0;
            checkpoint?.Invoke("DuringVerification", job);
            job.ActualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (job.HttpCheckpoint is { } checkpointState && job.ActualSha256 != checkpointState.PrefixSha256)
                throw new InvalidDataException("HTTP partial changed after its final checkpoint. Files were preserved.");
        }
        if (job.ExpectedSha256.Length > 0 && !job.ActualSha256.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 checksum mismatch.");
        token.ThrowIfCancellationRequested();
        job.State = "Verified";
    }

    public static void DiscardStaging(DownloadJob job)
    {
        if (DownloadCompletion.Unresolved(job)) throw new IOException("Verified completion evidence must be kept. Resolve this download before discarding staging.");
        if (job.HttpCheckpoint is not null && job.Completion?.Phase != "Completed" && !job.CleanupPending)
            throw new IOException("Retained HTTP partials require a persisted explicit discard or restart intent.");
        ValidatePaths(job);
        ValidateOwnership(job);
        foreach (var path in new[] { job.StagingPath, job.StagingPath + ".crdownload" })
            if (File.Exists(path)) File.Delete(path);
        var directory = Path.GetDirectoryName(job.StagingPath)!;
        var marker = Path.Combine(directory, ".owner");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(path => !path.Equals(marker, StringComparison.OrdinalIgnoreCase))) return;
        if (File.Exists(marker)) File.Delete(marker);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    // Read-only inspection: absence is not proof of ownership and never permits deletion.
    public static bool StagingIsMissing(DownloadJob job)
    {
        ValidatePaths(job);
        if (!Guid.TryParseExact(job.StagingOwner, "N", out _) || job.StagingRoot.Length == 0 ||
            !ImageFileSystem.SamePath(job.StagingRoot, Path.GetDirectoryName(job.Destination)!)) return false;
        var directory = Path.GetDirectoryName(job.StagingPath)!;
        return !Directory.Exists(directory) && !File.Exists(directory);
    }

    public static string InspectStaging(DownloadJob job)
    {
        if (job.Method == "Browser" && StagingIsMissing(job))
            return "Browser partial data is missing. Download again opens the provider for a fresh transfer; missing bytes cannot be resumed.";
        if (HasOwnedPartial(job)) return "Incomplete files retained. Keep this record, or select Discard incomplete.";
        return job.Method == "Browser"
            ? "Browser partial data is missing. Download again starts a fresh provider transfer; it cannot resume missing bytes. Select Discard incomplete to clear this record."
            : "Owned partial files are missing. Select Discard incomplete to clear the record.";
    }

    public static bool HasOwnedPartial(DownloadJob job)
    {
        ValidatePaths(job);
        ValidateOwnership(job);
        return File.Exists(job.StagingPath) || File.Exists(job.StagingPath + ".crdownload");
    }

    private static void ValidateOwnership(DownloadJob job)
    {
        if (!Guid.TryParseExact(job.StagingOwner, "N", out _) || job.StagingRoot.Length == 0 ||
            !Path.GetFullPath(job.StagingRoot).Equals(Path.GetDirectoryName(Path.GetFullPath(job.Destination)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Staging ownership is unknown. Keep the record and inspect the recorded path manually.");
        var marker = Path.Combine(Path.GetDirectoryName(job.StagingPath)!, ".owner");
        if (!File.Exists(marker))
        {
            // A persisted discard intent can outlive its marker. Only absent files and an empty directory can reconcile.
            var directory = Path.GetDirectoryName(job.StagingPath)!;
            if (job.CleanupPending && (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any())) return;
            throw new IOException("Staging ownership marker is missing. Keep the record and inspect the recorded path manually.");
        }
        if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe staging ownership marker.");
        var contents = File.ReadAllText(marker);
        if (contents != job.Id + ":" + job.StagingOwner && contents != job.Id + ":" + job.StagingOwner + "\n" + Reservation(job))
            throw new IOException("Staging ownership marker is unsafe or belongs to another job.");
    }

    private static string Reservation(DownloadJob job) => Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] {
        job.Id, job.EntryId, job.ArtifactId, job.SourceId, job.Url, Path.GetFullPath(job.Destination), Path.GetFullPath(job.StagingRoot), job.StagingOwner })));

    internal static void ValidateCompletionOwnership(DownloadJob job)
    {
        ValidatePaths(job); ValidateOwnership(job);
        var marker = Path.Combine(Path.GetDirectoryName(job.StagingPath)!, ".owner");
        if (File.ReadAllText(marker) != job.Id + ":" + job.StagingOwner + "\n" + Reservation(job))
            throw new IOException("The saved completion reservation cannot be proven. Keep the files for manual attention.");
        ImageFileSystem.EnsureNoLinks(job.Destination);
        if (new FileInfo(job.Destination).LinkTarget is not null || Directory.Exists(job.Destination)) throw new IOException("Unsafe final download path.");
        var name = Path.GetFileName(job.Destination);
        if (name != job.FileName || name != SafeName(name) || name.StartsWith('.')) throw new IOException("Invalid final download filename.");
    }

    private static void ValidatePaths(DownloadJob job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _)) throw new IOException("Invalid staging owner.");
        var root = Path.GetDirectoryName(Path.GetFullPath(job.Destination))!;
        var expected = Path.Combine(root, ".ps5catalog-staging", job.Id, "payload.part");
        if (!Path.GetFullPath(job.StagingPath).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid staging path.");
        NoReparsePoints(Path.GetDirectoryName(expected)!);
        foreach (var path in new[] { expected, expected + ".crdownload" })
            if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Unsafe staging file.");
    }

    private static void NoReparsePoints(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Download directories cannot contain filesystem links.");
    }
}
