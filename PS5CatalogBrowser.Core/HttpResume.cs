using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PS5CatalogBrowser.Core;

// HTTP checkpoints live only in settings.json. The existing .owner remains a reservation, not a journal.
public static class HttpResume
{
    public static string Identity(DownloadJob job, HttpResumeCheckpoint state) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        job.Id, job.SelectionIdentity, job.EntryId, job.SourceId, job.Source, job.ArtifactId, job.Url,
        job.FileName, job.ExpectedFileName, job.ExpectedSize, job.ExpectedSha256, job.Destination, job.StagingRoot, job.StagingPath, job.StagingOwner,
        job.GroupId, job.Title, job.Version, job.Method, job.OrbitRelease,
        Schema = state.Version, state.FileIdentity, state.StrongETag, state.TotalBytes
    })));

    public static bool Strong(string value) => EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";
    public static bool Eligible(DownloadJob job) => job.HttpCheckpoint is { Version: 1, RestartRequired: false, TotalBytes: > 0 } state &&
        Strong(state.StrongETag) && job.SelectionIdentity.Length > 0 && job.Url.Length > 0 && state.Cursor > 0 && state.Cursor < state.TotalBytes &&
        job.Method == "Direct" && job.Completion is null && !job.CleanupPending && !job.IsCleared;
    public static bool Reserves(DownloadJob job) => job.Method == "Direct" && job.HttpCheckpoint is { TotalBytes: > 0 } state &&
        Strong(state.StrongETag) && job.SelectionIdentity.Length > 0 &&
        job.Completion is null && !job.IsCleared && job.State is not ("Cancelled" or "Downloaded" or "Imported" or "Missing" or "Changed");

    public static void ValidateState(DownloadJob job)
    {
        var state = job.HttpCheckpoint ?? throw new IOException("No trusted HTTP checkpoint. Restart is required.");
        if (state.Version != 1 || state.Identity != Identity(job, state) || state.FileIdentity.Length == 0 ||
            state.Cursor < 0 || state.TotalBytes is < 0 || state.TotalBytes is long total && state.Cursor > total ||
            state.PrefixSha256.Length != 64 || !state.PrefixSha256.All(Uri.IsHexDigit) ||
            job.Method != "Direct" || job.Completion is not null || job.CleanupPending || job.IsCleared)
            throw new IOException("Saved HTTP identity or checkpoint is inconsistent. Files were preserved.");
        NetworkPolicy.Validate(job.Url);
        BrowserDownloadFiles.ValidateCompletionOwnership(job);
    }

    public static FileStream OpenMarker(DownloadJob job)
    {
        BrowserDownloadFiles.ValidateCompletionOwnership(job);
        var marker = new FileStream(Path.Combine(Path.GetDirectoryName(job.StagingPath)!, ".owner"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try { BrowserDownloadFiles.ValidateCompletionOwnership(job); return marker; }
        catch { marker.Dispose(); throw; }
    }

    public static FileStream OpenPartial(DownloadJob job, bool create = false)
    {
        BrowserDownloadFiles.ValidateCompletionOwnership(job);
        var stream = new FileStream(job.StagingPath, create ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var identity = FileIdentity(stream);
            if (!create && job.HttpCheckpoint is { } state && identity != state.FileIdentity)
                throw new IOException("Partial file identity changed. Files were preserved.");
            BrowserDownloadFiles.ValidateCompletionOwnership(job);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static void Inspect(DownloadJob job)
    {
        job.CanPause = job.CanResume = false;
        job.IsCleared = false;
        try
        {
            ValidateState(job);
            using var marker = OpenMarker(job);
            using var stream = OpenPartial(job);
            var state = job.HttpCheckpoint!;
            if (stream.Length < state.Cursor) throw new IOException("Partial file is shorter than its durable checkpoint. Files were preserved.");
            job.DownloadedBytes = state.Cursor; job.TotalBytes = state.TotalBytes; job.BytesPerSecond = 0;
            job.CanResume = Eligible(job) && job.State != "Cancelled";
            job.State = job.CanResume ? "Paused" : job.State is "Failed" or "Integrity failed" or "Cancelled" ? job.State : "Needs attention";
            if (job.CanResume) job.Error = "Owned partial retained. Resume validates its prefix and the server representation before appending.";
            else if (job.Error.Length == 0) job.Error = "This partial cannot safely resume. Keep it, or explicitly Restart or Discard incomplete.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        { job.State = "Needs attention"; job.Error = ex.Message; }
    }

    public static async Task HashPrefixAsync(FileStream stream, HttpResumeCheckpoint state, IncrementalHash hash, CancellationToken token)
    {
        if (stream.Length < state.Cursor) throw new IOException("Partial file is shorter than its durable checkpoint.");
        stream.Position = 0;
        var buffer = new byte[81920]; long remaining = state.Cursor;
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (count == 0) throw new IOException("Partial file ended before its checkpoint.");
            hash.AppendData(buffer, 0, count); remaining -= count;
        }
        if (!Convert.ToHexString(hash.GetCurrentHash()).Equals(state.PrefixSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Durable partial content changed. Files were preserved.");
    }

    public static void DiscardPartial(DownloadJob job)
    {
        if (job.Completion is not null) throw new IOException("Completed or finalising download evidence cannot be discarded.");
        if (job.HttpCheckpoint is null || !File.Exists(job.StagingPath)) { BrowserDownloadFiles.DiscardStaging(job); return; }
        // Delete the exact opened file, with its identity held against replacement until deletion completes.
        using (var marker = OpenMarker(job))
        using (var stream = OpenPartial(job))
        {
            var disposition = new FileDisposition { DeleteFile = true };
            // Reopen with DELETE access while retaining the same identity; OpenPartial cannot share that access.
            stream.Dispose();
            using var handle = CreateFile(job.StagingPath, 0x80000000u | 0x10000u, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("Cannot own partial for deletion.", new Win32Exception(Marshal.GetLastWin32Error()));
            BrowserDownloadFiles.ValidateCompletionOwnership(job);
            if (FileIdentity(handle) != job.HttpCheckpoint.FileIdentity) throw new IOException("Partial identity changed before deletion. Files were preserved.");
            if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                throw new IOException("Cannot delete owned partial.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        BrowserDownloadFiles.DiscardStaging(job);
    }

    public static string FileIdentity(FileStream stream) => FileIdentity(stream.SafeFileHandle);
    private static string FileIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Durable HTTP resume requires Windows file identity support.");
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot prove partial file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
            throw new IOException("Partial must be an ordinary file with one filesystem name.");
        return $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}:{info.CreationHigh:X8}{info.CreationLow:X8}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref FileDisposition information, uint size);
}
