using System.IO.MemoryMappedFiles;
using LibOrbisPkg.PFS;
using LibOrbisPkg.PKG;
using LibProsperoPkg.PKG;

namespace PS5CatalogBrowser.Core;

// Presents an extracted, privately owned package tree to the existing installation transaction.
// No console backend, executable patching or package creation is used.
public sealed class PackageImageMounter(string installationRoot, string passcode = "", IProgress<InstallationProgress>? progress = null) : IImageMounter
{
    public Task CheckAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    public async Task<IImageMount> MountAsync(string source, CancellationToken cancellation)
    {
        if (passcode.Length is not (0 or 32)) throw new InvalidDataException("A package passcode must contain exactly 32 characters.");
        var root = Path.Combine(Path.GetFullPath(installationRoot), ".package-read-" + Guid.NewGuid().ToString("N"));
        ImageFileSystem.EnsureNoLinks(root);
        var mount = new PackageTree(root);
        try
        {
            await Task.Run(() => Extract(source, root, cancellation), cancellation);
            return mount;
        }
        catch (LibProsperoPkg.PFS.ProsperoExtractionException ex)
        {
            await mount.DisposeAsync();
            throw new InvalidDataException("Cannot extract the PS5 package: " + ex.Message, ex);
        }
        catch { await mount.DisposeAsync(); throw; }
    }

    private void Extract(string source, string root, CancellationToken cancellation)
    {
        using var input = ImageFileSystem.OpenRead(source);
        var magic = new byte[4]; input.ReadExactly(magic); input.Position = 0;
        if (magic.AsSpan().SequenceEqual(new byte[] { 0x7f, 0x46, 0x49, 0x48 }))
        {
            var info = ProsperoPackageExtractor.Inspect(source);
            if (info.IsRetail) throw new InvalidDataException("Encrypted retail PS5 PKGs require matching image key material. This installer supports debug/fake PKGs with a known passcode.");
            var key = info.OuterEncrypted ? ProsperoExtractionKey.FromPasscode(passcode.Length == 0 ? new string('0', 32) : passcode) : ProsperoExtractionKey.None;
            var expected = ProsperoPackageExtractor.ListFiles(source, key, cancellation: cancellation);
            ExtractionPaths.Check(expected.Select(file => (file.RelativePath, file.Size)), root, spaceCopies: 2);
            cancellation.ThrowIfCancellationRequested();
            ProsperoPackageExtractor.Extract(source, root, key, logger: text =>
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(new(InstallationState.Mounting, "Reading PS5 package: " + text, 0, 0, 0, TimeSpan.Zero));
            }, cancellation: cancellation);
            var scan = ImageFileSystem.ScanAsync(root, cancellation, requireGame: false).GetAwaiter().GetResult();
            var wanted = expected.Select(file => (Path: ExtractionPaths.Relative(file.RelativePath), file.Size)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (scan.Files.Count != wanted.Length || scan.Files.Where((file, i) => !file.RelativePath.Equals(wanted[i].Path, StringComparison.OrdinalIgnoreCase) || file.Length != wanted[i].Size).Any())
                throw new InvalidDataException("PS5 extraction did not match the inspected package file set.");
        }
        else if (magic.AsSpan().SequenceEqual(new byte[] { 0x7f, 0x43, 0x4e, 0x54 }))
        {
            var reader = new PkgReader(input); var header = reader.ReadHeader();
            if (header.entry_count > 20000 || (ulong)header.entry_table_offset + (ulong)header.entry_count * 32 > (ulong)input.Length ||
                header.pfs_image_offset == 0 || header.pfs_image_offset >= (ulong)input.Length || header.pfs_image_size < 0x400 || header.pfs_image_size > (ulong)input.Length - header.pfs_image_offset)
                throw new InvalidDataException("PKG is truncated or has invalid table/filesystem bounds.");
            // Bound every metadata entry before the upstream reader allocates its buffers.
            input.Position = header.entry_table_offset;
            for (var i = 0; i < header.entry_count; i++)
            {
                var meta = MetaEntry.Read(input);
                if (meta.DataSize > 64 * 1024 * 1024 || (ulong)meta.DataOffset + meta.DataSize > (ulong)input.Length)
                    throw new InvalidDataException("PKG metadata is truncated or exceeds the metadata size limit.");
            }
            var pkg = reader.ReadPkg();
            var ekpfs = passcode.Length == 0 ? pkg.GetEkpfs() : LibOrbisPkg.Util.Crypto.ComputeKeys(header.content_id, passcode, 1);
            using var mapping = MemoryMappedFile.CreateFromFile(input, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            using var view = mapping.CreateViewAccessor((long)header.pfs_image_offset, (long)header.pfs_image_size, MemoryMappedFileAccess.Read);
            PfsReader outer;
            // PS4 package filesystems use the classic EKPFS/seed schedule. Passing package header flags
            // through enables the reader's different newCrypt schedule for otherwise readable fake PKGs.
            try { outer = new PfsReader(view, ekpfs: ekpfs); }
            catch (Exception ex)
            {
                var guidance = ekpfs is null ? "No automatic fake-package key was available; a known package passcode may be needed." :
                    passcode.Length > 0 ? "Check the supplied passcode and package integrity." :
                    "Automatic fake-package key recovery succeeded; this error does not establish a missing passcode. The filesystem may be damaged or unsupported.";
                throw new InvalidDataException("Cannot read the PS4 package filesystem: " + ex.Message + " " + guidance, ex);
            }
            var nested = outer.GetFile("pfs_image.dat");
            using var innerView = nested?.GetView();
            using var decompressed = innerView is null ? null : new PFSCReader(innerView);
            var files = (decompressed is null ? outer : new PfsReader(decompressed)).GetAllFiles().ToArray();
            var names = files.Select(file => Relative(file.FullName)).ToArray();
            var sizes = files.Select(file => file.size != file.compressed_size ? file.compressed_size : file.size).ToArray();
            var total = ExtractionPaths.Check(names.Select((name, i) => (name, sizes[i])), root, spaceCopies: 2);
            Directory.CreateDirectory(root);
            long done = 0; var buffer = new byte[1024 * 1024];
            for (var i = 0; i < files.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var destination = Path.Combine(root, names[i]); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var bytes = files[i].GetView();
                using var expanded = files[i].size != files[i].compressed_size ? new PFSCReader(bytes) : null;
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                for (long position = 0; position < sizes[i];)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var take = (int)Math.Min(buffer.Length, sizes[i] - position);
                    (expanded ?? bytes).Read(position, buffer, 0, take); output.Write(buffer, 0, take);
                    position += take; done += take;
                    progress?.Report(new(InstallationState.Mounting, "Reading PS4 package: " + names[i], done, total, 0, TimeSpan.Zero));
                }
                output.Flush(true);
            }
        }
        else throw new InvalidDataException("Not a supported PS4 CNT or PS5 FIH package.");
        cancellation.ThrowIfCancellationRequested();
    }

    private static string Relative(string fullName)
    {
        const string prefix = "/uroot/";
        if (!fullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package filesystem root.");
        return ExtractionPaths.Relative(fullName[prefix.Length..]);
    }

    private sealed class PackageTree(string root) : IImageMount
    {
        public string Root => root;
        public string Description => "Private package extraction (source read-only)";
        public async Task ReleaseAsync()
        {
            if (!Directory.Exists(root)) return;
            // Only this freshly allocated directory is owned. Reject links before any recursive cleanup.
            await ImageFileSystem.ScanAsync(root, CancellationToken.None, requireGame: false);
            Directory.Delete(root, true);
        }
        public async ValueTask DisposeAsync() => await ReleaseAsync();
    }
}
