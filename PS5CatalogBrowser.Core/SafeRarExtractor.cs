using SharpCompress.Archives.Rar;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Readers.Rar;

namespace PS5CatalogBrowser.Core;

public static class SafeRarExtractor
{
    public static async Task<string> ExtractAsync(string source, string destination, CancellationToken cancellation, IProgress<double>? progress = null, string? password = null)
    {
        destination = Path.GetFullPath(destination);
        ImageFileSystem.EnsureNoLinks(source); ImageFileSystem.EnsureNoLinks(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Extraction destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, ".ps5catalog-extract-" + Guid.NewGuid().ToString("N"));
        // Stream-based opening cannot discover or silently consume adjacent multipart volumes.
        using var stream = ImageFileSystem.OpenRead(source);
        var signature = new byte[7]; stream.ReadExactly(signature); stream.Position = 0;
        var legacyCrc = signature.AsSpan().SequenceEqual("Rar!\x1a\x07\0"u8);
        using var archive = RarArchive.Open(stream, new SharpCompress.Readers.ReaderOptions { Password = password });
        var entries = archive.Entries.Take(ExtractionPaths.MaximumEntries + 1).ToArray();
        if (password is null && entries.Any(entry => entry.IsEncrypted)) throw new ArchivePasswordException();
        if (entries.Length > ExtractionPaths.MaximumEntries) throw new InvalidDataException("RAR contains too many entries.");
        if (!archive.IsComplete || entries.Any(entry => entry.LinkTarget is not null || entry.IsSplitAfter || entry.VolumeIndexFirst != 0 || entry.VolumeIndexLast != 0 ||
            ((entry.Attrib ?? 0) & 0x400) != 0 || ((entry.Attrib ?? 0) & 0xF000) is 0xA000 or 0x2000 or 0x6000 ||
            ((entry.Attrib ?? 0) >> 16 & 0xF000) is 0xA000 or 0x2000 or 0x6000))
            throw new InvalidDataException("RAR must be complete and contain no links. Multipart archives are not supported.");
        var total = ExtractionPaths.Check(entries.Select(entry => (entry.Key ?? "", entry.IsDirectory ? 0 : entry.Size)), destination);
        Directory.CreateDirectory(staging);
        var readingEncryptedEntry = false;
        try
        {
            long copied = 0; var buffer = new byte[1024 * 1024]; progress?.Report(0);
            var lastReport = Environment.TickCount64;
            using var readerStream = ImageFileSystem.OpenRead(source);
            // The pinned library's input buffer can split encrypted blocks at its
            // boundary, causing valid RAR5 entries to decode as empty. FileStream
            // already buffers reads; disable only SharpCompress's extra buffer.
            using var reader = RarReader.Open(readerStream, new SharpCompress.Readers.ReaderOptions { Password = password, BufferSize = 0 });
            while (reader.MoveToNextEntry())
            {
                cancellation.ThrowIfCancellationRequested();
                readingEncryptedEntry = false;
                var entry = reader.Entry;
                var path = Path.Combine(staging, ExtractionPaths.Relative(entry.Key ?? ""));
                if (entry.IsDirectory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                readingEncryptedEntry = entry.IsEncrypted;
                using var input = reader.OpenEntryStream();
                try
                {
                    var crc = legacyCrc ? new CRC32() : null;
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true);
                    long remaining = entry.Size;
                    int read;
                    // SharpCompress 1.0.0's async RAR decoder can overrun output and
                    // report false CRC failures. The caller runs extraction on a worker.
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        remaining -= read;
                        if (remaining < 0) throw new InvalidDataException("RAR file exceeded its declared size.");
                        crc?.SlurpBlock(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation); copied += read;
                        if (Environment.TickCount64 - lastReport >= 100)
                        {
                            progress?.Report(total == 0 ? 0 : 100d * copied / total);
                            lastReport = Environment.TickCount64;
                        }
                    }
                    // The pinned library disables CRC verification for encrypted RARs.
                    // Legacy RAR stores the ordinary plaintext CRC, so verify it ourselves.
                    if (crc is not null && unchecked((uint)crc.Crc32Result) != unchecked((uint)entry.Crc))
                        throw new InvalidDataException("RAR file checksum mismatch; no content was published.");
                    if (remaining != 0) throw new InvalidDataException("RAR file ended early; no content was published.");
                    await output.FlushAsync(cancellation); output.Flush(true);
                }
                catch { reader.Cancel(); throw; }
            }
            readingEncryptedEntry = false;
            cancellation.ThrowIfCancellationRequested();
            ImageFileSystem.EnsureNoLinks(destination);
            Directory.Move(staging, destination);
            progress?.Report(100); return destination;
        }
        catch (Exception ex) when (readingEncryptedEntry && (ArchivePasswordException.IsPasswordFailure(ex) ||
            ex is InvalidDataException or SharpCompress.Common.InvalidFormatException ||
            ex is InvalidOperationException && ex.Message.StartsWith("unpacked file size does not match header:", StringComparison.Ordinal)))
        { throw new ArchivePasswordException(); }
        finally
        {
            if (Directory.Exists(staging))
            {
                await ImageFileSystem.ScanAsync(staging, CancellationToken.None, requireGame: false);
                Directory.Delete(staging, true);
            }
        }
    }
}
