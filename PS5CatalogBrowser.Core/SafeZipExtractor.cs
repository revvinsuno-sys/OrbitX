using SharpCompress.Archives.Zip;
using SharpCompress.Readers;
using SharpCompress.Compressors.Deflate;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public static class SafeZipExtractor
{
    public const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    public const int MaximumEntries = 20000;

    public static async Task<string> ExtractAsync(string zipPath, string destination, CancellationToken token, IProgress<double>? progress = null, string? password = null)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("ZIP file is missing.", zipPath);
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Extraction destination already exists.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".ps5catalog-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var readingEncryptedEntry = false;
        try
        {
            ImageFileSystem.EnsureNoLinks(zipPath); ImageFileSystem.EnsureNoLinks(destination);
            using var archiveStream = ImageFileSystem.OpenRead(zipPath);
            using var archive = ZipArchive.Open(archiveStream, new ReaderOptions { Password = password });
            var entries = archive.Entries.Take(MaximumEntries + 1).ToArray();
            if (entries.Length > MaximumEntries) throw new InvalidDataException("ZIP contains too many entries.");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Size < 0 || entry.CompressedSize < 0) throw new InvalidDataException("ZIP contains an invalid entry size.");
                var relative = ValidateName(entry.Key ?? "");
                var mode = ((entry.Attrib ?? 0) >> 16) & 0xF000;
                if (mode is 0xA000 or 0x2000 or 0x6000 || ((entry.Attrib ?? 0) & 0x400) != 0)
                    throw new InvalidDataException("ZIP contains a symbolic link or special file.");
                var path = Path.GetFullPath(Path.Combine(staging, relative));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ZIP path escapes extraction directory.");
                var isDirectory = (entry.Key ?? "").EndsWith('/');
                if (!targets.Add(path.TrimEnd(Path.DirectorySeparatorChar))) throw new InvalidDataException("ZIP contains duplicate destinations.");
                if (!isDirectory)
                {
                    total = checked(total + entry.Size);
                    if (total > MaximumExpandedBytes) throw new InvalidDataException("ZIP expands beyond the 8 GiB limit.");
                    if (entry.CompressedSize > 0 && entry.Size / entry.CompressedSize > 1000 && entry.Size > 100L * 1024 * 1024)
                        throw new InvalidDataException("ZIP entry has an excessive expansion ratio.");
                }
            }
            if (password is null && entries.Any(entry => entry.IsEncrypted)) throw new ArchivePasswordException();
            var free = new DriveInfo(Path.GetPathRoot(staging)!).AvailableFreeSpace;
            if (total > free) throw new IOException("Insufficient disk space for extraction.");
            long copied = 0;
            var lastReport = Environment.TickCount64;
            progress?.Report(0);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, ValidateName(entry.Key ?? ""));
                if ((entry.Key ?? "").EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                readingEncryptedEntry = entry.IsEncrypted;
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await using var input = entry.OpenEntryStream();
                var crc = new CRC32();
                var remaining = entry.Size;
                var block = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(block, token)) > 0)
                {
                    remaining -= count;
                    if (remaining < 0) throw new InvalidDataException("ZIP entry exceeded its declared length.");
                    await output.WriteAsync(block.AsMemory(0, count), token);
                    crc.SlurpBlock(block, 0, count);
                    copied += count;
                    if (Environment.TickCount64 - lastReport >= 100)
                    {
                        progress?.Report(total == 0 ? 0 : 100d * copied / total);
                        lastReport = Environment.TickCount64;
                    }
                }
                if ((!entry.IsEncrypted || entry.Crc != 0) && unchecked((uint)crc.Crc32Result) != unchecked((uint)entry.Crc))
                    throw new InvalidDataException("ZIP entry checksum failed; no content was published.");
                if (remaining != 0) throw new InvalidDataException("ZIP entry ended early.");
            }
            readingEncryptedEntry = false;
            ImageFileSystem.EnsureNoLinks(destination);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination)) throw new IOException("Extraction destination appeared during extraction.");
            Directory.Move(staging, destination);
            progress?.Report(100);
            return destination;
        }
        catch (Exception ex) when (ArchivePasswordException.IsPasswordFailure(ex) ||
            readingEncryptedEntry && ex is (InvalidDataException or SharpCompress.Common.ArchiveException or ZlibException))
        { throw new ArchivePasswordException(); }
        catch (SharpCompress.Common.ArchiveException ex) { throw new InvalidDataException("ZIP metadata is corrupt or incomplete.", ex); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\') || name.Contains('\\'))
            throw new InvalidDataException("ZIP contains an unsafe path.");
        var parts = name.TrimEnd('/').Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '<', '>', '"', '|', '?', '*', '\0']) >= 0 ||
                Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("ZIP contains an unsafe filename.");
        }
        return Path.Combine(parts);
    }
}
