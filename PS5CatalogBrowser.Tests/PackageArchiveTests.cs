using System.Buffers.Binary;
using System.IO;
using System.Text;
using LibOrbisPkg.PFS;
using PS5CatalogBrowser.Core;

internal static class PackageArchiveTests
{
    // Generated with WinRAR 7 from only the synthetic strings below; no commercial game data.
    internal const string Rar5 = "UmFyIRoHAQDz4YLrCwEFBwAGAQGAgIAAg6W8sSkCAwukAASUIyBin2ecgAUADWdhbWUvZGF0YS50eHQKAwJ6C7NZoVzdAca9ITMDL7MkrlroBIHJybkf6b3l7rp79V0sgSUQ1TNjzY82PJKLfdwqAgMLoAAEoAAgKh84UoAAAA5nYW1lL2Vib290LmJpbgoDAmrkslmhXN0Bc3ludGhldGljIHVuc3VwcG9ydGVkIGV4ZWN1dGFibGWcIQixMwIDC7sABLsAIJCVJ4aAAAAXZ2FtZS9zY2Vfc3lzL3BhcmFtLmpzb24KAwJq5LJZoVzdAXsidGl0bGVJZCI6IlBQU0EwMDAwMSIsInRpdGxlTmFtZSI6IlN5bnRoZXRpYyBSQVIgZml4dHVyZSJ9ySMcVyYCAwsABQAQAAAAAIAAAAxnYW1lL3NjZV9zeXMKAwJq5LJZoVzdAfL15aseAgMLAAUAEAAAAACAAAAEZ2FtZQoDAnoLs1mhXN0BHXdWUQMFBAA=";

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5PackageArchive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var regression = Path.Combine(root, "reader-regression.rar");
            await using (var fixture = typeof(PackageArchiveTests).Assembly.GetManifestResourceStream("rar5-reader-regression.rar") ?? throw new InvalidDataException("Missing RAR regression fixture"))
            await using (var output = File.Create(regression)) await fixture.CopyToAsync(output);
            var regressionOutput = Path.Combine(root, "reader-regression");
            await SafeRarExtractor.ExtractAsync(regression, regressionOutput, default);
            var regressionFile = Path.Combine(regressionOutput, "second.bin");
            Check(new FileInfo(regressionFile).Length == 16 * 1024 * 1024 &&
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(regressionFile))) == "DB185F2B3A8645DBD589496346FBC8CD8473234B57244C2AA0B6E922A9A2DA0E", "multi-buffer solid RAR5 exact length and SHA-256 regression");
            var encryptedRegression = Path.Combine(root, "encrypted-buffer-regression.rar");
            await using (var fixture = typeof(PackageArchiveTests).Assembly.GetManifestResourceStream("rar5-encrypted-buffer-regression.rar") ?? throw new InvalidDataException("Missing encrypted RAR regression fixture"))
            await using (var output = File.Create(encryptedRegression)) await fixture.CopyToAsync(output);
            var encryptedOutput = Path.Combine(root, "encrypted-buffer-regression");
            await ArchiveExtraction.ExtractAsync(encryptedRegression, encryptedOutput, ["wrong-password", "SyntheticArchivePassword"], default);
            foreach (var (name, size, hash) in new[]
            {
                ("first.txt", 43, "52169B26C3334D28BFDF4E3181E59642800E6F4B30C85D3F26EAD6C26D1E6AC8"),
                ("second.bin", 84880, "E2C3F679B6E8970CF2308D81C16BFE71E76FA703A6C987D9AC483556393F9F84"),
                ("third.bin", 786432, "67BFA2D1BC5E029EF307A65381C7894557720423F743109731B7191890A9E101")
            })
            {
                var file = Path.Combine(encryptedOutput, name);
                Check(new FileInfo(file).Length == size && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) == hash,
                    "encrypted RAR5 across input-buffer boundary exact length and SHA-256: " + name);
            }
            var rar = Path.Combine(root, "synthetic.rar"); File.WriteAllBytes(rar, Convert.FromBase64String(Rar5));
            var destination = Path.Combine(root, "rar-output");
            await SafeRarExtractor.ExtractAsync(rar, destination, default);
            Check(File.ReadAllText(Path.Combine(destination, "game", "data.txt")) == string.Concat(Enumerable.Repeat("synthetic data ", 300)), "RAR5 compressed payload round-trip");
            Check(File.Exists(rar), "RAR original preserved");
            await Reject(() => SafeRarExtractor.ExtractAsync(rar, destination, default));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); var cancelled = Path.Combine(root, "cancelled");
                await Reject(() => SafeRarExtractor.ExtractAsync(rar, cancelled, cancellation.Token));
                Check(!Directory.Exists(cancelled), "cancelled RAR not published");
            }
            foreach (var name in new[] { "../escape", "/absolute", "C:/escape", "data:stream", "CON", "trailing.", "safe.txt" })
            {
                var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".rar");
                WriteRar4(file, name, Encoding.UTF8.GetBytes("synthetic data"));
                var output = file + ".out";
                if (name == "safe.txt") { await SafeRarExtractor.ExtractAsync(file, output, default); Check(File.ReadAllText(Path.Combine(output, name)) == "synthetic data", "RAR4 stored round-trip"); }
                else { await Reject(() => SafeRarExtractor.ExtractAsync(file, output, default)); Check(!Directory.Exists(output), "unsafe RAR not published: " + name); }
            }
            var corrupt = Convert.FromBase64String(Rar5); corrupt[corrupt.Length / 2] ^= 0x20; File.WriteAllBytes(rar, corrupt);
            await Reject(() => SafeRarExtractor.ExtractAsync(rar, Path.Combine(root, "corrupt"), default));
            Check(!Directory.Exists(Path.Combine(root, "corrupt")), "corrupt RAR not published");
            var badPayload = Path.Combine(root, "bad-payload.rar"); WriteRar4(badPayload, "safe.txt", Encoding.ASCII.GetBytes("synthetic data"));
            var badBytes = File.ReadAllBytes(badPayload); badBytes[^8] ^= 0x20; File.WriteAllBytes(badPayload, badBytes);
            await Reject(() => SafeRarExtractor.ExtractAsync(badPayload, badPayload + ".out", default));
            Check(!Directory.Exists(badPayload + ".out"), "RAR payload CRC failure not published");
            Check(!Directory.EnumerateDirectories(root, ".ps5catalog-extract-*").Any(), "RAR staging cleaned");

            foreach (var platform in new[] { "ps4", "ps5" })
            foreach (var retain in new[] { false, true })
            {
                var source = Path.Combine(root, platform + retain + ".pkg"); BuildPackage(source, platform == "ps5");
                var before = File.ReadAllBytes(source); var installed = Path.Combine(root, platform + retain + "-installed");
                Directory.CreateDirectory(installed);
                await using (var tree = await new PackageImageMounter(installed).MountAsync(source, default))
                    Check(File.ReadAllText(Path.Combine(tree.Root, "eboot.bin")) == "synthetic unsupported executable", "package reader preserves payload");
                var journal = new InstallationJournal(Path.Combine(root, platform + retain + "-journal"));
                var store = new SettingsStore(Path.Combine(root, platform + retain + "-settings")); var settings = new AppSettings { Sources = [] };
                settings.Library.Add(new() { ContentPath = source, DownloadPath = source }); store.Save(settings);
                var job = new InstallationJob { SourcePath = source, InstallationRoot = installed, Title = "Synthetic " + platform, DeleteOriginal = !retain };
                var coordinator = new ImageInstallationCoordinator(new PackageImageMounter(installed), journal,
                    record => { InstalledLibrary.Register(settings, record); store.Save(settings); return Task.CompletedTask; },
                    record => Task.FromResult(InstalledLibrary.IsRegistered(store.Load(), record)), _ => false);
                await coordinator.InstallAsync(job, confirmation => Task.FromResult<InstallationChoice?>(new(confirmation.FolderName, !retain)), null, default);
                Check(job.Registered && job.Verified && job.State == (retain ? InstallationState.CompletedOriginalRetained : InstallationState.Completed), platform + " verified installation: " + job.Error);
                Check(File.ReadAllText(Path.Combine(job.Destination, "eboot.bin")) == "synthetic unsupported executable", "Kyty-incompatible executable still installed");
                Check(File.Exists(source) == retain && (!retain || File.ReadAllBytes(source).SequenceEqual(before)), "package source retention/deletion after verification");
                Check(!Directory.EnumerateDirectories(installed, ".package-read-*").Any(), "package scratch cleaned");
                await ImageFileSystem.VerifyFinalAsync(job.Destination, job, default);
            }
            var bad = Path.Combine(root, "bad.pkg"); BuildPackage(bad, false); using (var stream = new FileStream(bad, FileMode.Open, FileAccess.Write)) stream.SetLength(0x11000);
            await Reject(async () => { await using var ignored = await new PackageImageMounter(root).MountAsync(bad, default); });
            Check(File.Exists(bad) && !Directory.EnumerateDirectories(root, ".package-read-*").Any(), "invalid package preserves source and cleans staging");
            foreach (var ps5 in new[] { false, true })
            {
                var encrypted = Path.Combine(root, "encrypted-" + ps5 + ".pkg"); BuildPackage(encrypted, ps5, encrypted: true);
                await using (var tree = await new PackageImageMounter(root, new string('1', 32)).MountAsync(encrypted, default))
                    Check(File.ReadAllText(Path.Combine(tree.Root, "eboot.bin")) == "synthetic unsupported executable", "known-passcode encrypted package payload");
                await Reject(async () => { await using var ignored = await new PackageImageMounter(root, new string('2', 32)).MountAsync(encrypted, default); });
            }
            var fakePkg = Path.Combine(root, "fake-ps4.pkg");
            var fakeTree = new FSDir(); var fakeSys = new FSDir { name = "sce_sys", Parent = fakeTree }; fakeTree.Dirs.Add(fakeSys);
            var executable = Encoding.ASCII.GetBytes("synthetic unsupported executable"); fakeTree.Files.Add(new FSFile(stream => stream.Write(executable), "eboot.bin", executable.Length) { Parent = fakeTree });
            var sfo = new LibOrbisPkg.SFO.ParamSfo(); sfo["TITLE"] = new LibOrbisPkg.SFO.Utf8Value("TITLE", "Synthetic fake package", 128);
            using var sfoBytes = new MemoryStream(); sfo.Write(sfoBytes); var param = sfoBytes.ToArray();
            fakeSys.Files.Add(new FSFile(stream => stream.Write(param), "param.sfo", param.Length) { Parent = fakeSys });
            new LibOrbisPkg.PKG.PkgBuilder(new() { RootDir = fakeTree, ContentId = "UP0001-CUSA00001_00-SYNTHETIC0000000", Passcode = new string('0', 32), TimeStamp = DateTime.UnixEpoch, VolumeType = LibOrbisPkg.GP4.VolumeType.pkg_ps4_app }).Write(fakePkg, _ => { });
            await using (var tree = await new PackageImageMounter(root).MountAsync(fakePkg, default))
                Check(File.ReadAllBytes(Path.Combine(tree.Root, "eboot.bin")).SequenceEqual(executable), "full PS4 fake PKG default key extraction");
            Console.WriteLine("PASS RAR4/RAR5 compressed/stored extraction, unsafe names, CRC corruption, cancellation, PS4/PS5 package readers and verified installation with source retain/delete");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void BuildPackage(string path, bool ps5, bool encrypted = false, bool game = true)
    {
        var tree = new FSDir(); var sys = new FSDir { name = "sce_sys", Parent = tree }; tree.Dirs.Add(sys);
        if (game) Add(tree, "eboot.bin", "synthetic unsupported executable");
        Add(sys, "param.json", "{\"titleId\":\"PPSA00001\",\"titleName\":\"Synthetic package\"}");
        Add(tree, "data.txt", "synthetic package data");
        var inner = new PfsBuilder(new PfsProperties { root = tree, BlockSize = 0x10000, Seed = new byte[16] });
        var outerTree = new FSDir(); outerTree.Files.Add(new FSFile(inner) { Parent = outerTree });
        const string contentId = "UP0001-PPSA00001_00-SYNTHETIC0000000";
        var passcode = new string('1', 32);
        var outer = new PfsBuilder(new PfsProperties { root = outerTree, BlockSize = 0x10000, Seed = new byte[16], Encrypt = encrypted, EKPFS = LibOrbisPkg.Util.Crypto.ComputeKeys(contentId, passcode, 1) });
        using var image = new MemoryStream();
        if (ps5)
        {
            var ps5Tree = new LibProsperoPkg.PFS.ProsperoFsDir();
            var ps5Sys = new LibProsperoPkg.PFS.ProsperoFsDir { name = "sce_sys", Parent = ps5Tree }; ps5Tree.Dirs.Add(ps5Sys);
            if (game) AddPs5(ps5Tree, "eboot.bin", "synthetic unsupported executable");
            AddPs5(ps5Sys, "param.json", "{\"titleId\":\"PPSA00001\",\"titleName\":\"Synthetic package\"}");
            AddPs5(ps5Tree, "data.txt", "synthetic package data");
            var ps5Inner = new LibProsperoPkg.PFS.ProsperoPfsBuilder(new() { root = ps5Tree, BlockSize = 0x10000, Seed = new byte[16] });
            var ps5Outer = new LibProsperoPkg.PFS.ProsperoFsDir(); ps5Outer.Files.Add(new LibProsperoPkg.PFS.ProsperoFsFile(ps5Inner) { Parent = ps5Outer });
            var ps5Builder = new LibProsperoPkg.PFS.ProsperoPfsBuilder(new() { root = ps5Outer, BlockSize = 0x10000, Seed = new byte[16], Encrypt = encrypted, EKPFS = LibProsperoPkg.Util.Crypto.ComputeKeys(contentId, passcode, 1) });
            image.SetLength(ps5Builder.CalculatePfsSize()); ps5Builder.WriteImage(image);
        }
        else { image.SetLength(outer.CalculatePfsSize()); outer.WriteImage(image); }
        var header = new byte[0x10000];
        if (ps5)
        {
            new byte[] { 0x7f, 0x46, 0x49, 0x48 }.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x10), (ulong)header.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x18), (ulong)image.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x58), 0x200);
            new byte[] { 0x7f, 0x43, 0x4e, 0x54 }.CopyTo(header, 0x200);
            Encoding.ASCII.GetBytes(contentId).CopyTo(header, 0x240);
        }
        else
        {
            new byte[] { 0x7f, 0x43, 0x4e, 0x54 }.CopyTo(header, 0);
            // Real PS4 fake PKGs can carry this high header bit while using the classic PFS key schedule.
            if (encrypted) BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x408), 0xA0000000000003CCUL);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x410), (ulong)header.Length);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x418), (ulong)image.Length);
            Encoding.ASCII.GetBytes(contentId).CopyTo(header, 0x40);
        }
        using var output = File.Create(path); output.Write(header); image.Position = 0; image.CopyTo(output);
        static void Add(FSDir parent, string name, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); parent.Files.Add(new FSFile(stream => stream.Write(bytes), name, bytes.Length) { Parent = parent });
        }
        static void AddPs5(LibProsperoPkg.PFS.ProsperoFsDir parent, string name, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); parent.Files.Add(new LibProsperoPkg.PFS.ProsperoFsFile(stream => stream.Write(bytes), name, bytes.Length) { Parent = parent });
        }
    }

    private static void WriteRar4(string path, string name, byte[] bytes)
    {
        using var output = File.Create(path); output.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 7, 0 });
        Header(0x73, 0, new byte[6]);
        using var payload = new MemoryStream(); using var data = new BinaryWriter(payload);
        data.Write((uint)bytes.Length); data.Write((uint)bytes.Length); data.Write((byte)2); data.Write(Crc(bytes)); data.Write(0u); data.Write((byte)20); data.Write((byte)0x30);
        var encoded = Encoding.UTF8.GetBytes(name); data.Write((ushort)encoded.Length); data.Write(0x20u); data.Write(encoded);
        Header(0x74, 0x8000, payload.ToArray()); output.Write(bytes); Header(0x7b, 0, []);
        void Header(byte type, ushort flags, byte[] fields)
        {
            using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
            writer.Write(type); writer.Write(flags); writer.Write((ushort)(7 + fields.Length)); writer.Write(fields);
            var block = buffer.ToArray(); using var header = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            header.Write((ushort)Crc(block)); header.Write(block);
        }
        static uint Crc(byte[] buffer)
        {
            uint crc = uint.MaxValue;
            foreach (var value in buffer) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
            return ~crc;
        }
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or SharpCompress.Common.SharpCompressException) { return; }
        throw new Exception("Expected extraction to reject unsafe or incomplete input.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
