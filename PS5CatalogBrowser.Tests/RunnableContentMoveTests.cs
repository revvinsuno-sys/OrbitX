using PS5CatalogBrowser.Core;

internal static class RunnableContentMoveTests
{
    private sealed class ImmediateProgress(Action<InstallationProgress> report) : IProgress<InstallationProgress>
    { public void Report(InstallationProgress value) => report(value); }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5RunnableMove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var duringCommit in new[] { false, true })
            {
                var local = Path.Combine(root, "cancel-" + duringCommit);
                var source = Path.Combine(local, "downloads", "game"); Directory.CreateDirectory(source);
                File.WriteAllBytes(Path.Combine(source, "eboot.bin"), new byte[2 * 1024 * 1024]);
                var record = new LocalLibraryEntry { ContentPath = source, Extraction = "Extracted" };
                var settingsForCancel = new AppSettings { DownloadRoot = Path.GetDirectoryName(source)!, InstalledGamesRoot = Path.Combine(local, "installed"), Library = [record] };
                var cancelStore = new SettingsStore(Path.Combine(local, "settings")); cancelStore.Save(settingsForCancel);
                using var cancellation = new CancellationTokenSource(); var updates = new List<InstallationProgress>();
                var progress = new ImmediateProgress(update =>
                {
                    updates.Add(update);
                    if (duringCommit ? update.State == InstallationState.Finalising : update.Bytes > 0) cancellation.Cancel();
                });
                try { await RunnableContentMove.MoveAsync(settingsForCancel, record, cancelStore.Save, _ => false, cancellation.Token, progress); }
                catch (OperationCanceledException) when (!duringCommit) { }
                Check(updates.Any(update => update.Bytes > 0 && update.TotalBytes > 0 && update.File.Contains("eboot.bin")), "verification reports current file and byte progress");
                Check(duringCommit ? record.ContentMove?.Phase == "Completed" && Directory.Exists(record.ContentPath) : Directory.Exists(source) && record.ContentMove is null,
                    "cancellation preserves source before rename and finishes durable registration after rename");
            }
            var store = new SettingsStore(Path.Combine(root, "file-settings"));
            var settings = new AppSettings { Sources = [], DownloadRoot = Path.Combine(root, "file-downloads"), InstalledGamesRoot = Path.Combine(root, "file-installed") };
            var bytes = SyntheticExFat.TestExecutable();
            var entry = new CatalogEntry { Id = "runnable", Title = "Synthetic executable" };
            var job = BrowserDownloadFiles.Prepare(entry, new() { Artifact = new() { Id = "elf", Url = "https://fixture.invalid/file" }, Url = "https://fixture.invalid/file" }, settings.DownloadRoot, "game.elf", []);
            File.WriteAllBytes(job.StagingPath, bytes); job.DownloadedBytes = bytes.Length;
            settings.Downloads.Add(job); store.Save(settings);
            Directory.CreateDirectory(settings.InstalledGamesRoot);
            var existingElf = Path.Combine(settings.InstalledGamesRoot, "game.elf"); File.WriteAllText(existingElf, "existing executable preserved");
            var item = await DownloadCompletion.CompleteAsync(job, settings, store.Save, default);
            Check(job.State == "Installed" && job.IsCleared && !File.Exists(job.Destination) && File.ReadAllBytes(item.ContentPath).SequenceEqual(bytes), "automatic ELF relocation follows verified completion");
            Check(Path.GetFileName(item.ContentPath) == "game (1).elf" && File.ReadAllText(existingElf) == "existing executable preserved", "ELF collision preserves the original and retains the executable extension");
            Check(item.ContentMove?.Phase == "Completed" && item.DownloadArtifacts.Single().Destination == item.ContentPath, "relocation and current library path persist together");
            BrowserDownloadFiles.DiscardStaging(job);
            var saved = store.LoadForStartup();
            Check(saved.Downloads.Single().State == "Installed" && saved.Library.Single().ContentPath == item.ContentPath, "restart preserves immutable completion reservation and installed path");
            Check(await DownloadCompletion.CompleteAsync(job, settings, store.Save, default) == item, "duplicate completion does not duplicate or move again");

            foreach (var mode in new[] { "folder", "invalid", "collision", "collision-race", "active", "save-before", "save-after", "tampered-after", "pending-source", "shared", "reconciled" })
            {
                var downloadRoot = Path.Combine(root, mode, "downloads");
                var installedRoot = Path.Combine(root, mode, "installed");
                var source = Path.Combine(downloadRoot, "game"); Directory.CreateDirectory(source);
                File.WriteAllBytes(Path.Combine(source, "eboot.bin"), mode == "invalid" ? new byte[] { 1, 2, 3 } : bytes);
                File.WriteAllText(Path.Combine(source, "asset.dat"), "synthetic asset");
                var localStore = new SettingsStore(Path.Combine(root, mode, "settings"));
                var record = new LocalLibraryEntry { ContentPath = source, Title = "Synthetic folder" };
                if (mode == "folder")
                {
                    record.DownloadPath = Path.Combine(downloadRoot, "retained.zip");
                    File.WriteAllText(record.DownloadPath, "synthetic retained archive");
                    record.DownloadArtifacts.Add(new() { Destination = record.DownloadPath });
                }
                var localSettings = new AppSettings { Sources = [], DownloadRoot = downloadRoot, InstalledGamesRoot = installedRoot, Library = [record] };
                if (mode == "shared") localSettings.Library.Add(new() { Title = "Real shared folder", ContentPath = source });
                if (mode == "reconciled")
                {
                    localSettings.Library.Add(new() { Title = "Legacy parent container", ContentPath = Path.GetDirectoryName(downloadRoot)! });
                    Check(InstalledLibrary.RetireObsoleteReferences(localSettings, localStore.Save) == 1, "obsolete parent reference retired before move");
                }
                if (mode == "active") localSettings.Downloads.Add(new() { Destination = source, State = "Downloading" });
                if (mode == "collision")
                {
                    Directory.CreateDirectory(Path.Combine(installedRoot, "game")); File.WriteAllText(Path.Combine(installedRoot, "game", "keep"), "unrelated");
                    File.WriteAllText(Path.Combine(installedRoot, "game (1)"), "existing file");
                    localSettings.Library.Add(new() { ContentPath = Path.Combine(installedRoot, "game (2)") });
                }
                localStore.Save(localSettings);
                var faulted = false;
                localStore.BeforeSave = candidate =>
                {
                    var move = candidate.Library[0].ContentMove;
                    if (!faulted && (mode == "save-before" && move?.Phase == "Pending" || (mode is "save-after" or "tampered-after") && move?.Phase == "Completed"))
                    { faulted = true; throw new IOException("Synthetic move persistence fault"); }
                };
                try
                {
                    var moved = await RunnableContentMove.MoveAsync(localSettings, record, candidate =>
                    {
                        localStore.Save(candidate);
                        if (mode == "collision-race" && candidate.Library[0].ContentMove is { Phase: "Pending" } move)
                        { Directory.CreateDirectory(move.Destination); File.WriteAllText(Path.Combine(move.Destination, "keep"), "racing content"); }
                        if (mode == "pending-source" && candidate.Library[0].ContentMove?.Phase == "Pending")
                            throw new OperationCanceledException("Synthetic crash after intent save, before rename");
                    }, _ => mode == "active");
                    Check(moved == (mode is "folder" or "invalid" or "collision" or "reconciled"), "unowned content moves independently of Kyty compatibility: " + mode);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    Check(mode is "collision-race" or "active" or "save-before" or "save-after" or "tampered-after" or "pending-source" or "shared", "unexpected move failure: " + mode);
                    if (mode == "shared") Check(ex.Message.Contains("Real shared folder") && ex.Message.Contains(source), "move conflict names actual shared entry and path");
                }
                localStore.BeforeSave = null;
                if (mode is "folder" or "invalid" or "collision" or "reconciled")
                {
                    Check(!Directory.Exists(source) && File.Exists(Path.Combine(record.ContentPath, "asset.dat")), "whole runnable folder moved without dropping assets");
                    await RunnableContentMove.VerifyAsync(record.ContentMove!, record.ContentPath, default);
                    var deletion = LibraryDeletion.Prepare(localSettings, record, [], _ => false);
                    Check(deletion.Count == 1 && deletion[0].Path == record.ContentPath && (mode != "folder" || File.Exists(Path.Combine(downloadRoot, "retained.zip"))),
                        "deleting moved content cannot include its retained source archive");
                    if (mode == "collision")
                        Check(record.ContentPath == Path.Combine(installedRoot, "game (3)") && File.ReadAllText(Path.Combine(installedRoot, "game", "keep")) == "unrelated" &&
                            File.ReadAllText(Path.Combine(installedRoot, "game (1)")) == "existing file" && localStore.LoadForStartup().Library[0].ContentPath == record.ContentPath,
                            "folder/file collisions and retained library paths are skipped without overwriting or merging; selected path survives restart");
                }
                else if (mode == "save-after")
                {
                    Check(faulted && !Directory.Exists(source), "post-rename fault retains pending recovery evidence");
                    var recovered = localStore.LoadForStartup();
                    Check(recovered.Library[0].ContentMove?.Phase == "Completed" && Directory.Exists(recovered.Library[0].ContentPath), "restart verifies and registers the renamed folder");
                    Check(localStore.LoadForStartup().Library.Count == 1, "recovery is idempotent");
                }
                else if (mode == "tampered-after")
                {
                    var destination = Path.Combine(installedRoot, "game");
                    File.WriteAllText(Path.Combine(destination, "asset.dat"), "replacement data preserved");
                    var recovered = localStore.LoadForStartup();
                    Check(recovered.Library[0].ContentMove?.Phase == "Pending" && recovered.Library[0].Classification.StartsWith("Content move needs attention") &&
                        File.ReadAllText(Path.Combine(destination, "asset.dat")) == "replacement data preserved", "changed destination is neither registered nor removed");
                }
                else if (mode == "pending-source")
                    Check(localStore.LoadForStartup().Library[0].ContentMove?.Phase == "Pending" && Directory.Exists(source) && !Directory.Exists(Path.Combine(installedRoot, "game")),
                        "interrupted intent never automatically deletes or moves the source");
                else
                {
                    Check(Directory.Exists(source), "unsafe/uncommitted source preserved: " + mode);
                    if (mode == "collision-race") Check(File.ReadAllText(Path.Combine(installedRoot, "game", "keep")) == "racing content" && record.ContentMove is null,
                        "a destination created after intent persistence is never overwritten and the source remains retryable");
                }
            }
            var otherVolume = Path.Combine(Environment.CurrentDirectory, ".runnable-cross-volume-" + Guid.NewGuid().ToString("N"));
            if (!string.Equals(Path.GetPathRoot(otherVolume), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(otherVolume);
                try
                {
                    var source = Path.Combine(root, "cross-downloads", "game"); Directory.CreateDirectory(source);
                    File.WriteAllBytes(Path.Combine(source, "eboot.bin"), bytes);
                    var crossStore = new SettingsStore(Path.Combine(root, "cross-settings"));
                    var record = new LocalLibraryEntry { ContentPath = source };
                    var crossSettings = new AppSettings { DownloadRoot = Path.GetDirectoryName(source)!, InstalledGamesRoot = otherVolume, Library = [record] };
                    crossStore.Save(crossSettings);
                    try { await RunnableContentMove.MoveAsync(crossSettings, record, crossStore.Save, _ => false); throw new Exception("Cross-volume move was accepted"); }
                    catch (IOException) { }
                    Check(Directory.Exists(source) && !Directory.Exists(Path.Combine(otherVolume, "game")), "cross-volume rename keeps the source without a copy/delete fallback");
                }
                finally { Directory.Delete(otherVolume, true); }
            }
            Console.WriteLine("PASS content installation: ELF completion, folder assets, immutable A4 evidence, restart, collision, Kyty-incompatible executable accepted, active/shared ownership and persistence faults");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
