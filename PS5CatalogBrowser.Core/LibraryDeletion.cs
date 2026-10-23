using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PS5CatalogBrowser.Core;

public static class LibraryDeletion
{
    private static IEnumerable<string> EntryPaths(LocalLibraryEntry item) =>
        item.ContentMove is { Phase: "Completed" } ? new[] { item.ContentPath } : new[] { item.ContentPath, item.DownloadPath, item.InstalledGame?.Directory ?? "" }
            .Concat(item.AdditionalPaths).Concat(item.DownloadArtifacts.Select(artifact => artifact.Destination));

    private static string[] Paths(LocalLibraryEntry item)
    {
        var paths = EntryPaths(item).Where(path => path.Length > 0).Select(path =>
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Library deletion requires an absolute local path: " + path);
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Where(path => !paths.Any(parent => ImageFileSystem.IsWithin(parent, path))).ToArray();
    }

    private static bool Overlaps(string a, string b) => b.Length > 0 &&
        (ImageFileSystem.SamePath(a, b) || ImageFileSystem.IsWithin(a, b) || ImageFileSystem.IsWithin(b, a));

    private static bool RegisteredDownload(LocalLibraryEntry item, DownloadJob job) =>
        item.DownloadArtifacts.Any(artifact => artifact.JobId == job.Id) ||
        item.DownloadRegistrationId.Length > 0 && item.DownloadRegistrationId == (job.GroupId.Length > 0 ? job.GroupId : job.Id);

    private static void Validate(AppSettings settings, LocalLibraryEntry item, string[] paths,
        IEnumerable<string> protectedRoots, Func<DownloadJob, bool> isActive)
    {
        if (!settings.Library.Contains(item)) throw new InvalidOperationException("This item is no longer in the library.");
        if (item.ContentMove is { Phase: not "Completed" }) throw new IOException("Resolve the incomplete content move before deletion.");
        foreach (var path in paths)
        {
            if (ImageFileSystem.SamePath(path, Path.GetPathRoot(path)!) ||
                new[] { settings.DownloadRoot, settings.InstalledGamesRoot.Length > 0 ? settings.InstalledGamesRoot : Path.Combine(settings.DownloadRoot, "Installed"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                    .Where(root => root.Length > 0).Any(root => ImageFileSystem.SamePath(path, root) || ImageFileSystem.IsWithin(path, root)) ||
                protectedRoots.Any(root => Overlaps(path, root)) ||
                path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals(".ps5catalog-staging", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A library, application, settings or staging directory cannot be deleted: " + path);
            var conflict = settings.Library.Where(other => other != item).SelectMany(other => EntryPaths(other)
                .Where(otherPath => Overlaps(path, otherPath)).Select(otherPath => (Item: other, Path: otherPath))).FirstOrDefault();
            if (conflict.Item is not null)
                throw new IOException($"Library item '{conflict.Item.Title}' references '{conflict.Path}', which overlaps '{path}'. Files were kept.");
            foreach (var job in settings.Downloads)
            {
                if (Overlaps(path, job.StagingPath)) throw new IOException("Download staging must be discarded from Downloads, not My Library.");
                if ((RegisteredDownload(item, job) || Overlaps(path, job.Destination)) &&
                    (isActive(job) || DownloadCompletion.Unresolved(job) || HttpResume.Reserves(job) || job.CleanupPending ||
                     job.State is "Queued" or "Downloading" or "Paused" or "Checking integrity" or "Verifying download" or "Verified" or "Cancelling"))
                    throw new IOException("An active or incomplete download owns this item. Finish or discard it before deletion.");
            }
            ImageFileSystem.EnsureNoLinks(path);
        }
    }

    public static List<LibraryDeletionTarget> Prepare(AppSettings settings, LocalLibraryEntry item,
        IEnumerable<string> protectedRoots, Func<DownloadJob, bool> isActive)
    {
        var paths = Paths(item);
        Validate(settings, item, paths, protectedRoots, isActive);
        if (item.DeletionTargets is { } pending)
        {
            if (!pending.Select(target => target.Path).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The library paths changed after deletion began. Keep the recovery record for manual attention.");
            return pending.Select(target => target with { }).ToList();
        }
        return paths.Select(Capture).ToList();
    }

    public static void Delete(AppSettings settings, LocalLibraryEntry item, List<LibraryDeletionTarget> confirmed,
        IEnumerable<string> protectedRoots, Func<DownloadJob, bool> isActive, Action<AppSettings> persist,
        Action<LibraryDeletionTarget> recycle)
    {
        var paths = Paths(item);
        if (!confirmed.Select(target => target.Path).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Library paths changed after confirmation. Confirm deletion again.");
        Validate(settings, item, paths, protectedRoots, isActive);
        foreach (var target in confirmed.Where(target => !target.Completed)) CheckIdentity(target);
        var previous = item.DeletionTargets;
        item.DeletionTargets = confirmed;
        try { persist(settings); } // Record the exact confirmed paths and identities before touching files.
        catch { item.DeletionTargets = previous; throw; }
        foreach (var target in confirmed.Where(target => !target.Completed))
        {
            Validate(settings, item, paths, protectedRoots, isActive);
            var current = CheckIdentity(target);
            if (current.FileIdentity.Length > 0)
            {
                recycle(target);
                if (File.Exists(target.Path) || Directory.Exists(target.Path)) throw new IOException("Windows did not remove the selected path: " + target.Path);
            }
            target.Completed = true;
            persist(settings);
        }
        var index = settings.Library.IndexOf(item);
        var candidate = SettingsStore.Snapshot(settings);
        candidate.Library.RemoveAt(index);
        foreach (var job in candidate.Downloads.Where(job => RegisteredDownload(item, job) || job.Destination.Length > 0 &&
            paths.Any(path => ImageFileSystem.SamePath(path, job.Destination) || ImageFileSystem.IsWithin(path, job.Destination))))
        {
            job.IsCleared = true;
            if (!File.Exists(job.Destination) && !Directory.Exists(job.Destination))
            { job.State = "Missing"; job.Error = "Local files deleted from My Library; download entry removed."; }
        }
        persist(candidate);
        settings.Library.RemoveAt(index);
        foreach (var job in settings.Downloads)
        {
            var saved = candidate.Downloads.First(other => other.Id == job.Id);
            job.State = saved.State; job.Error = saved.Error; job.IsCleared = saved.IsCleared;
        }
    }

    private static LibraryDeletionTarget CheckIdentity(LibraryDeletionTarget expected)
    {
        var current = Capture(expected.Path);
        if (current.FileIdentity.Length > 0 && (current.FileIdentity != expected.FileIdentity || current.IsDirectory != expected.IsDirectory))
            throw new IOException("The selected path was replaced after confirmation. Its files were kept: " + expected.Path);
        return current;
    }

    private static LibraryDeletionTarget Capture(string path)
    {
        ImageFileSystem.EnsureNoLinks(path);
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return new(path, false, "");
            throw new IOException("Cannot inspect the selected path: " + path, new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot identify the selected path: " + path);
        if ((info.Attributes & 0x400) != 0) throw new IOException("Links cannot be deleted through My Library: " + path);
        var directory = (info.Attributes & 0x10) != 0;
        if (directory)
        {
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var folder))
            {
                ImageFileSystem.EnsureNoLinks(folder);
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The selected folder contains a link. Its files were kept: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }
        return new(path, directory, $"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{info.CreationHigh:X8}{info.CreationLow:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
