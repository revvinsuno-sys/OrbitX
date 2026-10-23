using System.Text;
using PS5CatalogBrowser.Core;

internal static class InstallationTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Source identity/deletion tests require Windows.");
        await Naming(); await Verification(); await Coordinator(); await Interrupted(); await StagingOwnership(); await Ownership(); await Executables(); SyntheticFixture();
    }
    private static async Task Executables()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "executable-fixture.bin");
        var elf = SyntheticExFat.TestExecutable(); File.WriteAllBytes(path, elf); await GameExecutableInspection.ValidateAsync(path, default);
        foreach (var (offset, value) in new[] { (4, 1), (5, 2), (7, 0), (18, 3), (54, 0) })
        {
            var bad = (byte[])elf.Clone(); bad[offset] = (byte)value; File.WriteAllBytes(path, bad);
            await Reject(() => GameExecutableInspection.ValidateAsync(path, default));
        }
        File.WriteAllBytes(path, elf[..64]); await Reject(() => GameExecutableInspection.ValidateAsync(path, default));
        var self = new byte[elf.Length + 64]; new byte[] { 0x4f, 0x15, 0x3d, 0x1d, 0, 1, 1, 0x12, 1, 1, 0, 0 }.CopyTo(self, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(self.AsSpan(16), (ulong)self.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(self.AsSpan(24), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(self.AsSpan(26), 0x22);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(self.AsSpan(32), 0x800);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(self.AsSpan(40), 184);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(self.AsSpan(48), 32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(self.AsSpan(56), 32);
        elf.CopyTo(self, 64); File.WriteAllBytes(path, self); await GameExecutableInspection.ValidateAsync(path, default);
        self[5] = 0; File.WriteAllBytes(path, self); await Reject(() => GameExecutableInspection.ValidateAsync(path, default));
        Console.WriteLine("PASS KytyPS5 structural ELF/SELF eligibility and malformed/truncated executable rejection");
    }
    private static async Task Ownership()
    {
        using var fixture = new Fixture();
        using (var owned = new InstallationSource(fixture.Source))
        {
            Check(!InstallationSource.CanOwn(fixture.Source), "Another job can own an active source.");
            await Reject(() => { File.WriteAllText(fixture.Source, "replacement"); return Task.CompletedTask; });
        }
        Check(InstallationSource.CanOwn(fixture.Source), "Source ownership was not released.");
        Check(OSFMountProtocol.FreeDrive(d => d != "Q:") == "Q:", "Free drive selection failed.");
        await Reject(() => { OSFMountProtocol.FreeDrive(_ => true); return Task.CompletedTask; });
        const string source = @"C:\fixture\ReadOnly.exfat";
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(fixture.Root))!;
        Check(ImageFileSystem.IsWithin(volumeRoot, Path.Combine(volumeRoot, "nested")), "Mounted volume root rejects a valid child path.");
        Check(!ImageFileSystem.IsWithin(volumeRoot, volumeRoot), "Volume root counted as its own child.");
        Check(!ImageFileSystem.IsWithin(fixture.Root, fixture.Root + "-other"), "Sibling prefix counted as owned staging.");
        Check(!ImageFileSystem.IsWithin(fixture.Root, Path.Combine(fixture.Root, "..", "outside")), "Traversal escaped the owned root.");
        var prefix = "Mount point: Q:\r\nImage file: \\??\\" + source + "\r\nSize: 67108864 bytes (64 MB), ";
        var descriptor = prefix + "ReadOnly, File Type Virtual Disk, HDD.\r\n";
        Check(OSFMountProtocol.IsOwnedDescription(descriptor, "Q:", source), "Observed OSFMount 3.3 descriptor rejected.");
        Check(OSFMountProtocol.IsOwnedDescription(descriptor.Replace("Image file: ", "Image file:\r\r\n"), "Q:", source), "Observed long-image-path soft wrap rejected.");
        Check(OSFMountProtocol.IsOwnedDescription(descriptor.Replace(", File Type", ",\r\r\nFile Type"), "Q:", source), "Soft-wrapped drive properties rejected.");
        Check(!OSFMountProtocol.IsOwnedDescription(descriptor.Replace("Image file: ", "Image file:\r\n"), "Q:", source), "Ordinary extra descriptor line accepted as a soft wrap.");
        Check(!OSFMountProtocol.IsOwnedDescription(prefix + "ReadWrite, File Type Virtual Disk, HDD.", "Q:", source), "Filename confused read-only property validation.");
        Check(!OSFMountProtocol.IsOwnedDescription(prefix + "ReadOnly, File Type Virtual Disk, HDD.", "Q:", source + ".other"), "Foreign source mount accepted.");
        Check(!OSFMountProtocol.IsOwnedDescription(descriptor, "R:", source), "Foreign drive mount accepted.");
        Check(!OSFMountProtocol.IsOwnedDescription(descriptor + "Mount point: Q:\r\n", "Q:", source), "Duplicate drive descriptor accepted.");
        Check(!OSFMountProtocol.IsOwnedDescription(descriptor + "Image file: \\??\\" + source + "\r\n", "Q:", source), "Duplicate image descriptor accepted.");
        Check(!OSFMountProtocol.IsOwnedDescription(prefix + "ReadOnly, ReadWrite, File Type Virtual Disk, HDD.", "Q:", source), "Conflicting access flags accepted.");
        Check(!OSFMountProtocol.IsOwnedDescription(descriptor.Replace("Image file:", "Unknown file:"), "Q:", source), "Missing image identity accepted.");
        Check(!OSFMountProtocol.IsOwnedDescription("Q: = \\??\\" + source + "\nSize: 67108864 bytes (64 MB), ReadOnly, File Type Virtual Disk, HDD.", "Q:", source), "Unsupported historical descriptor accepted.");
        var store = new SettingsStore(Path.Combine(fixture.Root, "writer-test"));
        using (var settingsOwner = store.AcquireApplicationOwnership())
            await Reject(() => { using var competing = store.AcquireApplicationOwnership(); return Task.CompletedTask; });
        using var reacquired = store.AcquireApplicationOwnership();
        Console.WriteLine("PASS source ownership locks, blocked replacement, drive exhaustion and strict mount descriptor ownership");
    }
    private static void SyntheticFixture()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "full-filesystem.exfat"); SyntheticExFat.Create(path);
        Check(InstallationSource.IsCandidate(path) && new FileInfo(path).Length == 64L * 1024 * 1024, "Generated exFAT fixture is not a plausible complete image.");
        using var stream = File.OpenRead(path); var boot = new byte[12 * 512]; stream.ReadExactly(boot); var backup = new byte[boot.Length]; stream.ReadExactly(backup);
        Check(boot.SequenceEqual(backup), "Main and backup boot regions differ.");
        uint checksum = 0;
        for (var i = 0; i < 11 * 512; i++) if (i is not (106 or 107 or 112)) checksum = ((checksum >> 1) | (checksum << 31)) + boot[i];
        for (var i = 11 * 512; i < boot.Length; i += 4)
            Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(i)) == checksum, "Invalid boot region checksum.");
        var sectors = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(72));
        var heap = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88));
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92));
        Check((heap + count * (1u << boot[109])) <= sectors && sectors * 512 == (ulong)stream.Length, "Invalid fixture filesystem geometry.");
        Console.WriteLine("PASS generated 64 MiB synthetic exFAT fixture geometry and boot-region checksums (driver mount tested separately)");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> work)
    {
        try { await work(); } catch (IOException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Expected filesystem/verification rejection.");
    }
    private static async Task Naming()
    {
        using var f = new Fixture();
        var scan = await ImageFileSystem.ScanAsync(f.Mounted, default);
        Check(scan.Title == "ASTRO's PLAYROOM" && scan.TitleId == "PPSA01325", "PS5 metadata parsing failed.");
        foreach (var text in new[] { "../outside", "..\\..\\outside", "C:\\escape", "CON", "NUL.txt", "Bad<>:\"/\\|?*", "Trailing. ", "...", "LPT1" })
        {
            var name = ImageFileSystem.FolderName(text); var destination = ImageFileSystem.Destination(f.Installed, name);
            Check(ImageFileSystem.IsWithin(f.Installed, destination) && !name.Contains(".."), "Unsafe metadata folder name.");
        }
        await Reject(() => { ImageFileSystem.Destination(f.Installed, "../escape"); return Task.CompletedTask; });
        Directory.CreateDirectory(Path.Combine(f.Installed, "Existing"));
        await Reject(() => { ImageFileSystem.Destination(f.Installed, "Existing"); return Task.CompletedTask; });
        File.Delete(Path.Combine(f.Game, "sce_sys", "param.json")); File.WriteAllText(Path.Combine(f.Game, "sce_sys", "resource"), "resource");
        scan = await ImageFileSystem.ScanAsync(f.Mounted, default); Check(scan.Title == "", "Missing metadata was invented.");
    }
    private static async Task Verification()
    {
        foreach (var fault in new[] { "match", "missing", "size", "same-size", "truncated-large", "extra-file", "extra-directory", "source-change" })
        {
            using var f = new Fixture();
            var scan = await ImageFileSystem.ScanAsync(f.Mounted, default);
            var output = Path.Combine(f.Root, "verify"); Directory.CreateDirectory(output);
            await ImageFileSystem.CopyAsync(f.Mounted, output, scan, null, default);
            var target = Path.Combine(output, "nested", "game", "z-data.bin");
            switch (fault)
            {
                case "missing": File.Delete(target); break;
                case "size": File.AppendAllText(target, "changed"); break;
                case "same-size": using (var s = new FileStream(target, FileMode.Open, FileAccess.Write)) s.WriteByte(99); break;
                case "truncated-large": using (var s = new FileStream(target, FileMode.Open, FileAccess.Write)) s.SetLength(1024 * 1024); break;
                case "extra-file": File.WriteAllText(Path.Combine(output, "extra.bin"), "unexpected"); break;
                case "extra-directory": Directory.CreateDirectory(Path.Combine(output, "unexpected-empty")); break;
            }
            var mutated = false;
            var progress = new ImmediateProgress(update =>
            {
                if (fault == "source-change" && !mutated && update.Bytes > 0)
                {
                    mutated = true;
                    File.AppendAllText(Path.Combine(f.Game, "z-data.bin"), "changed during verification");
                }
            });
            if (fault == "match")
            {
                var hashes = await ImageFileSystem.VerifyAsync(f.Mounted, output, scan, progress, default);
                Check(hashes.Count == scan.Files.Count && hashes.All(file => file.Sha256.Length == 64), "Full hashes were not retained.");
            }
            else await Reject(() => ImageFileSystem.VerifyAsync(f.Mounted, output, scan, progress, default));
            Check(File.Exists(f.Source), "Verification fault removed original image.");
        }
    }
    private static async Task Coordinator()
    {
        foreach (var fault in new[] { "valid-delete", "retain", "no-metadata-catalogue", "no-metadata-filename", "duplicate-title-id", "malicious-metadata",
            "missing-image", "corrupt-image", "missing-osfmount", "mount-failure", "no-drive-letters", "no-space", "copy-interruption",
            "verification-failure", "cancel-mount", "cancel-scan", "cancel-copy", "cancel-verify", "confirmation-cancel", "finalisation-failure",
            "persistence-failure", "persistence-reread-failure", "identity-changed", "cleanup-in-use", "release-failure", "missing-eboot", "bad-eboot" })
        {
            using var f = new Fixture(); var journal = new InstallationJournal(Path.Combine(f.Root, "jobs"));
            var mount = new FakeMounter(f.Mounted); var store = new SettingsStore(Path.Combine(f.Root, "settings"));
            var settings = new AppSettings(); settings.Library.Add(new() { Title = "Old image record", ContentPath = f.Source, DownloadPath = f.Source });
            settings.Downloads.Add(new() { Destination = f.Source, State = "Downloaded", DownloadedBytes = new FileInfo(f.Source).Length });
            using var cancellation = new CancellationTokenSource();
            var job = new InstallationJob { SourcePath = f.Source, InstallationRoot = f.Installed, DeleteOriginal = fault != "retain", Title = "Catalogue title", TitleId = "PPSA99999" };
            if (fault.StartsWith("no-metadata", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(f.Game, "sce_sys", "param.json"), "{}");
            if (fault == "no-metadata-filename") job.Title = "";
            if (fault == "duplicate-title-id")
            {
                var previous = Path.Combine(f.Installed, "ASTRO's PLAYROOM"); Directory.CreateDirectory(previous);
                File.WriteAllText(Path.Combine(previous, "existing-game-data"), "preserve");
                settings.Library.Add(new() { Title = "ASTRO's PLAYROOM", ContentPath = previous, InstalledGame = new() { TitleId = "PPSA99999", Directory = previous } });
            }
            if (fault == "malicious-metadata") File.WriteAllText(Path.Combine(f.Game, "sce_sys", "param.json"), "{\"localizedParameters\":{\"en-US\":{\"titleName\":\"../../escape\"}}}");
            if (fault == "missing-image") File.Delete(f.Source);
            if (fault == "corrupt-image") File.WriteAllText(f.Source, "not an image");
            if (fault == "missing-osfmount") mount.CheckError = "OSFMount missing";
            if (fault is "mount-failure" or "no-drive-letters") mount.MountError = fault;
            if (fault == "cancel-mount") mount.AfterMount = () => cancellation.Cancel();
            if (fault == "missing-eboot") File.Delete(Path.Combine(f.Game, "eboot.bin"));
            if (fault == "bad-eboot") File.WriteAllText(Path.Combine(f.Game, "eboot.bin"), "not executable");
            if (fault == "release-failure") mount.ReleaseError = true;
            var sourceBefore = File.Exists(f.Source) ? File.ReadAllBytes(f.Source) : null;
            FileStream? externalReader = null; var registrationCalled = false;
            var coordinator = new ImageInstallationCoordinator(mount, journal, installation =>
            {
                registrationCalled = true;
                Check(mount.Released && File.Exists(f.Source) && installation.Verified, "Deletion occurred before verification, release or registration.");
                if (fault == "persistence-failure") throw new IOException("Disk full saving library");
                InstalledLibrary.Register(settings, installation); store.Save(settings);
                if (fault == "cleanup-in-use") externalReader = new FileStream(f.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.CompletedTask;
            }, installation => Task.FromResult(fault != "persistence-reread-failure" && InstalledLibrary.IsRegistered(store.Load(), installation)),
            _ => false, _ => fault == "no-space" ? 1 : long.MaxValue);
            var injected = false;
            var progress = new ImmediateProgress(update =>
            {
                if (fault == "cancel-scan" && update.State == InstallationState.Scanning) cancellation.Cancel();
                if (fault == "cancel-copy" && update.State == InstallationState.Extracting && update.Bytes > 0) cancellation.Cancel();
                if (fault == "cancel-verify" && update.State == InstallationState.Verifying && update.Bytes > 0) cancellation.Cancel();
                if (fault == "copy-interruption" && update.State == InstallationState.Extracting && update.Bytes > 0) throw new IOException("Simulated interrupted copy");
                if (fault == "verification-failure" && !injected && update.State == InstallationState.Verifying)
                {
                    injected = true; var target = Path.Combine(job.StagingPath, "content", "nested", "game", "z-data.bin");
                    using var file = new FileStream(target, FileMode.Open, FileAccess.Write); file.WriteByte(99);
                }
                if (fault == "finalisation-failure" && update.State == InstallationState.Finalising)
                { Directory.CreateDirectory(job.Destination); File.WriteAllText(Path.Combine(job.Destination, "existing-user-data"), "preserve"); }
                if (fault == "identity-changed" && update.State == InstallationState.RemovingOriginal)
                { File.Move(f.Source, f.Source + ".old"); File.Copy(f.Source + ".old", f.Source); }
            });
            await coordinator.InstallAsync(job, confirmation =>
            {
                if (fault == "confirmation-cancel") return Task.FromResult<InstallationChoice?>(null);
                if (fault == "no-metadata-catalogue") Check(confirmation.Title == "Catalogue title", "Catalogue title fallback failed.");
                if (fault == "no-metadata-filename") Check(confirmation.Title == "PPSA01325", "Filename fallback failed.");
                if (fault == "duplicate-title-id") Check(confirmation.FolderName.Contains("PPSA01325", StringComparison.Ordinal), "Duplicate titles did not use image title ID.");
                return Task.FromResult<InstallationChoice?>(new(confirmation.FolderName, job.DeleteOriginal));
            }, progress, cancellation.Token);
            var success = fault is "valid-delete" or "retain" or "no-metadata-catalogue" or "no-metadata-filename" or "duplicate-title-id" or "malicious-metadata" or "bad-eboot";
            if (success)
            {
                Check(job.State == (fault == "retain" ? InstallationState.CompletedOriginalRetained : InstallationState.Completed), "Unexpected terminal state for " + fault + ": " + job.Error);
                Check(job.Verified && job.Registered && registrationCalled, "Success without verified persistent registration.");
                var restored = store.Load(); Check(InstalledLibrary.IsRegistered(restored, job), "Installed library did not survive restart.");
                var emulator = Path.Combine(f.Root, "kyty_emulator.exe"); File.WriteAllText(emulator, "launcher fixture");
                var launch = KytyLauncher.BuildStartInfo(emulator, restored.Library[0].ContentPath);
                Check(launch.ArgumentList.SequenceEqual(new[] { "--game", Path.Combine(job.Destination, "nested", "game") }), "Launcher received the image instead of the extracted game root.");
                Check(restored.Library.Count == (fault == "duplicate-title-id" ? 2 : 1) && restored.Library[0].DownloadPath == "" && Directory.Exists(restored.Library[0].ContentPath), "Broken image library reference remained.");
                if (fault == "duplicate-title-id") Check(restored.Library[1].InstalledGame!.TitleId == "PPSA99999" && File.ReadAllText(Path.Combine(restored.Library[1].ContentPath, "existing-game-data")) == "preserve", "Different-ID game was replaced or modified.");
                Check(File.Exists(f.Source) == (fault == "retain"), "Incorrect source deletion option result.");
                if (fault == "retain") Check(File.ReadAllBytes(f.Source).SequenceEqual(sourceBefore!), "Retained source contents changed.");
                InstalledLibrary.UpdateCleanup(settings, job); store.Save(settings);
                Check(store.Load().Downloads[0].State == (job.SourceDeleted ? "Installed — original removed" : "Installed — original retained"), "Download record was not moved to the installed state after verified installation.");
                Check(store.Load().Downloads[0].IsCleared == job.SourceDeleted, "Downloads must hide only a confirmed deleted installation source.");
                Console.WriteLine("PASS image installation: " + fault);
            }
            else
            {
                Check(!job.SourceDeleted && (fault == "missing-image" || File.Exists(f.Source)), "Fault removed source: " + fault);
                if (sourceBefore is not null) Check(File.ReadAllBytes(f.Source).SequenceEqual(sourceBefore), "Fault modified source bytes: " + fault);
                Check(job.State is InstallationState.Failed or InstallationState.Cancelled or InstallationState.NeedsAttention or InstallationState.CompletedOriginalRetained, "Fault reported success: " + fault);
                if (fault == "finalisation-failure") Check(File.ReadAllText(Path.Combine(job.Destination, "existing-user-data")) == "preserve", "Existing destination overwritten.");
                if (fault == "cleanup-in-use")
                {
                    InstalledLibrary.UpdateCleanup(settings, job);
                    Check(!settings.Downloads[0].IsCleared, "Failed source cleanup must keep the download visible.");
                    Check(job.Registered && Directory.Exists(job.Destination), "Installed output lost when cleanup failed.");
                    externalReader!.Dispose(); externalReader = null;
                    await coordinator.RetryCleanupAsync(job, default); Check(job.SourceDeleted && !File.Exists(f.Source), "Explicit reconciled cleanup retry failed.");
                    InstalledLibrary.UpdateCleanup(settings, job); store.Save(settings);
                    Check(store.Load().Downloads[0].IsCleared && store.Load().Library.Count == 1, "Successful cleanup retry must hide the download and preserve the installation.");
                }
                Console.WriteLine("PASS source preservation: " + fault);
            }
            externalReader?.Dispose();
            Check(job.StagingPath.Length == 0 || !Directory.Exists(job.StagingPath), "Owned staging was not cleaned.");
        }
    }
    private static Task Interrupted()
    {
        using var f = new Fixture(); var journal = new InstallationJournal(Path.Combine(f.Root, "journal"));
        var job = new InstallationJob { SourcePath = f.Source, InstallationRoot = f.Installed, State = InstallationState.RemovingOriginal, Verified = true, Registered = true, DeleteOriginal = true };
        journal.Save(job); var loaded = journal.Load().Single();
        Check(loaded.State == InstallationState.NeedsAttention && File.Exists(f.Source), "Restart resumed deletion or reported interrupted work as successful.");
        return Task.CompletedTask;
    }
    private static async Task StagingOwnership()
    {
        using var f = new Fixture(); var job = new InstallationJob { InstallationRoot = f.Installed };
        job.StagingPath = Path.Combine(f.Installed, "unowned"); Directory.CreateDirectory(job.StagingPath); File.WriteAllText(Path.Combine(job.StagingPath, "owner"), job.Id);
        await Reject(() => { ImageInstallationCoordinator.CleanStaging(job); return Task.CompletedTask; });
        Check(Directory.Exists(job.StagingPath), "Unowned directory removed.");
        job.StagingPath = Path.Combine(f.Installed, ".install-" + job.Id); Directory.CreateDirectory(job.StagingPath);
        File.WriteAllText(Path.Combine(job.StagingPath, "owner"), "somebody-else");
        await Reject(() => { ImageInstallationCoordinator.CleanStaging(job); return Task.CompletedTask; });
        Check(Directory.Exists(job.StagingPath), "Mismatched marker removed.");
        File.WriteAllText(Path.Combine(job.StagingPath, "owner"), job.Id); File.WriteAllText(Path.Combine(job.StagingPath, "unrelated"), "preserve");
        await Reject(() => { ImageInstallationCoordinator.CleanStaging(job); return Task.CompletedTask; });
        Check(File.ReadAllText(Path.Combine(job.StagingPath, "unrelated")) == "preserve", "Non-manifest staging data was removed.");
    }
    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PS5InstallationTest-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "PPSA01325.exfat");
        public string Mounted => Path.Combine(Root, "mounted");
        public string Game => Path.Combine(Mounted, "nested", "game");
        public string Installed => Path.Combine(Root, "Installed");
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Game, "sce_sys")); Directory.CreateDirectory(Path.Combine(Game, "empty")); Directory.CreateDirectory(Installed);
            var image = new byte[16 * 1024]; Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(image, 3); image[108] = 9; image[110] = 1; image[510] = 0x55; image[511] = 0xaa;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(72), 32);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(80), 24);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(84), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(88), 25);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(92), 7);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(96), 2); File.WriteAllBytes(Source, image);
            File.WriteAllBytes(Path.Combine(Game, "eboot.bin"), SyntheticExFat.TestExecutable());
            File.WriteAllText(Path.Combine(Game, "sce_sys", "param.json"), "{\"titleId\":\"PPSA01325\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"ASTRO's PLAYROOM\"}}}");
            File.WriteAllBytes(Path.Combine(Game, "z-data.bin"), new byte[3 * 1024 * 1024]);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class FakeMounter(string root) : IImageMounter
    {
        public string CheckError = "", MountError = ""; public bool ReleaseError, Released; public Action? AfterMount;
        public Task CheckAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); if (CheckError.Length > 0) throw new IOException(CheckError); return Task.CompletedTask; }
        public Task<IImageMount> MountAsync(string source, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); if (MountError.Length > 0) throw new IOException(MountError);
            AfterMount?.Invoke(); return Task.FromResult<IImageMount>(new FakeMount(root, this));
        }
        private sealed class FakeMount(string root, FakeMounter owner) : IImageMount
        {
            public string Root => root;
            public string Description => "Synthetic fixture mount";
            public Task ReleaseAsync() { if (owner.ReleaseError) throw new IOException("Synthetic mount release failed"); owner.Released = true; return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class ImmediateProgress(Action<InstallationProgress> callback) : IProgress<InstallationProgress> { public void Report(InstallationProgress value) => callback(value); }
}
