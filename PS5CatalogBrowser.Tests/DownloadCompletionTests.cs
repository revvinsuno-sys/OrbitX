using System.Security.Cryptography;
using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class DownloadCompletionTests
{
    private sealed class Crash : Exception;
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PS5Completion-" + Guid.NewGuid().ToString("N"));
        public byte[] Bytes { get; } = Enumerable.Range(0, 16384).Select(i => (byte)(i % 251)).ToArray();
        public SettingsStore Store { get; }
        public AppSettings Settings { get; }
        public DownloadJob Job { get; }
        public Fixture(string method)
        {
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "settings"));
            var entry = new CatalogEntry { Id = "source:entry", SourceId = "source", SourceName = "Synthetic", Title = "Fixture", Version = "original" };
            var artifact = new CatalogArtifact { Id = "selected-part", Url = "https://fixture.test/original", Size = Bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant() };
            Job = BrowserDownloadFiles.Prepare(entry, new() { Artifact = artifact, Url = artifact.Url }, Path.Combine(Root, "downloads"), "fixture.pkg", []);
            Job.SelectionIdentity = DownloadAdmission.Capture(entry, artifact); Job.Method = method;
            Job.DownloadedBytes = Bytes.Length; Job.TotalBytes = Bytes.Length;
            File.WriteAllBytes(Job.StagingPath, Bytes);
            Settings = new() { DownloadRoot = Path.Combine(Root, "downloads"), Downloads = [Job], Sources = [] };
            Store.Save(Settings);
        }
        public Task<LocalLibraryEntry> Complete(Action<string, DownloadJob>? hook = null, CancellationToken token = default) =>
            DownloadCompletion.CompleteAsync(Job, Settings, Store.Save, token, hook);
        public AppSettings Restart()
        {
            using var ownership = Store.AcquireApplicationOwnership();
            return new SettingsStore(Store.DirectoryPath).LoadForStartup();
        }
        public void VerifyComplete(AppSettings recovered)
        {
            var job = recovered.Downloads.Single(); var item = recovered.Library.Single();
            Check(job.Completion?.Phase == "Completed" && job.State == (Job.Method == "External import" ? "Imported" : "Downloaded"), "Durable completion state");
            Check(!File.Exists(job.StagingPath) && File.ReadAllBytes(job.Destination).SequenceEqual(Bytes), "Actual promotion bytes");
            var metadata = item.DownloadArtifacts.Single();
            Check(metadata.JobId == Job.Id && metadata.SelectionIdentity == Job.SelectionIdentity && metadata.ArtifactId == Job.ArtifactId &&
                metadata.SourceId == Job.SourceId && metadata.Length == Bytes.Length && metadata.Sha256 == Job.ExpectedSha256 &&
                metadata.FileName == Job.FileName && metadata.Destination == Job.Destination && item.Version == "original", "Original artifact registration metadata");
            using var disk = JsonDocument.Parse(File.ReadAllText(Store.FilePath));
            Check(disk.RootElement.GetProperty("Downloads")[0].GetProperty("Completion").GetProperty("Phase").GetString() == "Completed" &&
                disk.RootElement.GetProperty("Library").GetArrayLength() == 1, "Persisted JSON agrees with filesystem and memory");
            var persisted = File.ReadAllBytes(Store.FilePath);
            var repeated = Restart();
            Check(repeated.Library.Count == 1 && File.ReadAllBytes(Store.FilePath).SequenceEqual(persisted), "Repeated restart performs no transaction replay");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    public static async Task RunAsync()
    {
        foreach (var method in new[] { "Direct", "Browser", "External import" })
        {
            foreach (var boundary in new[] { "BeforeVerification", "DuringVerification", "AfterVerification", "AfterIntent", "BeforeMove", "AfterMove", "AfterPromoted", "BeforeRegistrationSave", "AfterCompleted" })
            {
                using var f = new Fixture(method);
                await CrashAt(f, boundary);
                var recovered = f.Restart();
                if (boundary is "BeforeVerification" or "DuringVerification" or "AfterVerification")
                {
                    Check(recovered.Library.Count == 0 && recovered.Downloads.Single().Completion is null && File.Exists(f.Job.StagingPath) && !File.Exists(f.Job.Destination), "Pre-intent interruption retains bytes without inventing verification: " + boundary);
                    Check(recovered.Downloads.Single().State == "Interrupted", "Unverified writer is interrupted");
                }
                else f.VerifyComplete(recovered);
            }
            // Failure at each durable save, followed by an actual reload with fault injection removed.
            foreach (var phase in new[] { "Pending", "Promoted", "Completed" })
            {
                using var f = new Fixture(method);
                var fired = false;
                f.Store.BeforeSave = candidate =>
                {
                    if (!fired && candidate.Downloads.Single().Completion?.Phase == phase)
                    { fired = true; throw new IOException("Injected " + phase + " save failure"); }
                };
                await Reject(() => f.Complete());
                Check(fired && f.Job.State == "Needs attention" && f.Settings.Library.Count == 0, "Failure remains visible; no unsaved library entry");
                if (phase == "Pending") Check(File.Exists(f.Job.StagingPath) && !File.Exists(f.Job.Destination), "Intent persistence failure never promotes");
                else Check(!File.Exists(f.Job.StagingPath) && File.ReadAllBytes(f.Job.Destination).SequenceEqual(f.Bytes), "Promoted bytes survive save failure");
                f.VerifyComplete(f.Restart());
            }
            using (var f = new Fixture(method))
            {
                f.Store.BeforeSave = candidate => { if (candidate.Downloads.Single().Completion is not null) throw new IOException("Storage unavailable"); };
                await Reject(() => f.Complete());
                var recovered = f.Restart();
                Check(recovered.Library.Count == 0 && File.ReadAllBytes(f.Job.StagingPath).SequenceEqual(f.Bytes), "Persistent intent-save failure retains known staging without automatic recovery claims");
            }
        }

        using (var f = new Fixture("Direct"))
        {
            BrowserDownloadFiles.DiscardStaging(f.Job); f.Job.StagingPath = f.Job.StagingRoot = f.Job.StagingOwner = "";
            f.Job.DownloadedBytes = 0;
            using var server = new MockServer(f.Bytes); using var transfer = new HttpTransfer();
            f.Job.Url = server.Url("/file"); BrowserDownloadFiles.PrepareStaging(f.Job); f.Store.Save(f.Settings);
            await Reject(() => transfer.DownloadAsync(f.Job, new SyncProgress<DownloadJob>(job =>
                { if (job.State == "Verified") throw new IOException("Completion notification failed"); }), CancellationToken.None));
            f.Store.Save(f.Settings);
            Check(File.ReadAllBytes(f.Job.StagingPath).SequenceEqual(f.Bytes) && f.Restart().Library.Count == 0,
                "Notification failure after direct verification preserves bytes and the owned staging record");
        }
        // File.Move failure with a real Windows sharing lock, not a mocked filesystem.
        using (var f = new Fixture("Browser"))
        {
            FileStream? locked = null;
            try { await Reject(() => f.Complete((point, job) => { if (point == "BeforeMove") locked = new(job.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read); })); }
            finally { locked?.Dispose(); }
            Check(File.Exists(f.Job.StagingPath) && !File.Exists(f.Job.Destination), "Failed rename preserves staging");
            f.VerifyComplete(f.Restart());
        }
        foreach (var conflict in new[] { "both-matching", "both-different", "neither", "staging-changed", "final-changed", "identity", "checksum", "destination", "owner", "cleanup" })
        {
            using var f = new Fixture("Direct");
            await CrashAt(f, conflict == "final-changed" ? "AfterMove" : "AfterIntent");
            var saved = f.Store.Load(); var job = saved.Downloads.Single();
            var changed = f.Bytes.ToArray(); changed[changed.Length / 2] ^= 0x7f;
            switch (conflict)
            {
                case "both-matching": File.WriteAllBytes(job.Destination, f.Bytes); break;
                case "both-different": File.WriteAllBytes(job.Destination, changed); break;
                case "neither": File.Delete(job.StagingPath); break;
                case "staging-changed": File.WriteAllBytes(job.StagingPath, changed); break;
                case "final-changed": File.WriteAllBytes(job.Destination, changed); break;
                case "identity": job.ArtifactId = "unselected"; break;
                case "checksum": job.Completion!.Sha256 = new string('0', 64); break;
                case "destination": job.Destination = Path.Combine(f.Root, "escape.pkg"); break;
                case "owner": File.WriteAllText(Path.Combine(Path.GetDirectoryName(job.StagingPath)!, ".owner"), "unowned"); break;
                case "cleanup": job.CleanupPending = true; break;
            }
            f.Store.Save(saved);
            var stagingBefore = File.Exists(f.Job.StagingPath) ? File.ReadAllBytes(f.Job.StagingPath) : null;
            var finalBefore = File.Exists(f.Job.Destination) ? File.ReadAllBytes(f.Job.Destination) : null;
            var recovered = f.Restart();
            Check(recovered.Downloads.Single().State == "Needs attention" && recovered.Downloads.Single().Error.Length > 0 && recovered.Library.Count == 0, "Reject uncertain recovery: " + conflict);
            Check(EqualBytes(f.Job.StagingPath, stagingBefore) && EqualBytes(f.Job.Destination, finalBefore), "Conflict preserves both actual paths: " + conflict);
            Check(f.Restart().Library.Count == 0, "Repeated conflicting restart is idempotent");
        }
        using (var f = new Fixture("Browser"))
        {
            var collision = new byte[] { 12, 34, 56 };
            await Reject(() => f.Complete((point, job) => { if (point == "BeforeMove") File.WriteAllBytes(job.Destination, collision); }));
            Check(File.ReadAllBytes(f.Job.Destination).SequenceEqual(collision) && File.Exists(f.Job.StagingPath), "Reservation collision never overwrites");
            Check(f.Restart().Library.Count == 0, "Collision never registers existing data");
        }
        using (var f = new Fixture("Browser"))
        {
            var registrations = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.Complete()));
            Check(registrations.Select(item => item.Id).Distinct().Count() == 1 && f.Settings.Library.Count == 1, "Concurrent duplicate completion registers once");
            f.VerifyComplete(f.Restart());
        }
        foreach (var boundary in new[] { "BeforeVerification", "DuringVerification", "AfterVerification", "AfterIntent", "BeforeMove", "AfterMove", "AfterPromoted" })
        {
            using var f = new Fixture("Direct"); using var cancellation = new CancellationTokenSource();
            try { await f.Complete((point, _) => { if (point == boundary) cancellation.Cancel(); }, cancellation.Token); }
            catch (OperationCanceledException) when (boundary is "BeforeVerification" or "DuringVerification" or "AfterVerification") { }
            if (boundary is "BeforeVerification" or "DuringVerification" or "AfterVerification")
                Check(File.Exists(f.Job.StagingPath) && f.Restart().Library.Count == 0, "Cancellation before durable intent retains content");
            else f.VerifyComplete(f.Restart());
        }
        using (var f = new Fixture("Direct"))
        {
            await CrashAt(f, "AfterIntent");
            var pending = f.Store.Load().Downloads.Single();
            RejectAction(() => BrowserDownloadFiles.DiscardStaging(pending));
            Check(File.ReadAllBytes(pending.StagingPath).SequenceEqual(f.Bytes), "A3 cleanup cannot delete incomplete promotion");
            var admission = new DownloadAdmission(); admission.RestoreCompletions([pending]); admission.Release(pending);
            RejectAction(() => admission.Admit([pending.SelectionIdentity], () => [new()], _ => { }, _ => { }));
            Check(admission.Owns(pending), "A2 recovery admission remains exclusive");
            await DownloadCompletion.RecoverAsync(pending, new() { Downloads = [pending] }, _ => throw new IOException("Persistence unavailable"));
            Check(admission.Owns(pending), "Failed recovery retains A2 owner");
        }
        using (var f = new Fixture("Direct"))
        {
            var second = BrowserDownloadFiles.Prepare(new() { Id = f.Job.EntryId, SourceId = f.Job.SourceId, SourceName = f.Job.Source, Title = f.Job.Title },
                new() { Artifact = new() { Id = "part-two", Url = f.Job.Url }, Url = f.Job.Url }, f.Settings.DownloadRoot, "second.pkg", [f.Job]);
            f.Job.GroupId = second.GroupId = Guid.NewGuid().ToString("N");
            second.Method = "Direct"; second.DownloadedBytes = f.Bytes.Length; File.WriteAllBytes(second.StagingPath, f.Bytes);
            f.Settings.Downloads.Add(second); f.Store.Save(f.Settings);
            await f.Complete();
            Check(f.Settings.Library.Single().Classification == "Multiple files — download incomplete", "Partial batch is explicitly incomplete");
            try
            {
                await DownloadCompletion.CompleteAsync(second, f.Settings, f.Store.Save, CancellationToken.None,
                    (point, _) => { if (point == "AfterMove") throw new Crash(); });
            }
            catch (Crash) { }
            var recovered = f.Restart(); var library = recovered.Library.Single();
            Check(recovered.Downloads.All(job => job.Completion?.Phase == "Completed") && library.DownloadArtifacts.Count == 2 &&
                library.DownloadPath == f.Job.Destination && library.AdditionalPaths.SequenceEqual([second.Destination]) &&
                library.Classification == "Multiple files — assembly unsupported", "Batch restart preserves one registration and ordered verified parts");
            Check(f.Restart().Library.Single().DownloadArtifacts.Count == 2, "Batch registration is idempotent");
        }
        using (var f = new Fixture("Browser"))
        {
            await CrashAt(f, "AfterIntent");
            var stagingDirectory = Path.GetDirectoryName(f.Job.StagingPath)!; var relocated = stagingDirectory + "-real";
            Directory.Move(stagingDirectory, relocated);
            var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", stagingDirectory, relocated }) command.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(command)!) { process.WaitForExit(); Check(process.ExitCode == 0, "Synthetic completion junction created"); }
            try
            {
                Check(f.Restart().Downloads.Single().State == "Needs attention" && f.Restart().Library.Count == 0 &&
                    File.ReadAllBytes(Path.Combine(relocated, "payload.part")).SequenceEqual(f.Bytes), "Reparse ancestor prevents promotion and preserves target bytes");
            }
            finally { Directory.Delete(stagingDirectory); }
        }
        // A completed transaction is not replayed after installation has consumed its source.
        using (var f = new Fixture("Direct"))
        {
            await f.Complete();
            BrowserDownloadFiles.DiscardStaging(f.Job);
            File.Delete(f.Job.Destination);
            f.Job.State = "Installed — original removed"; f.Settings.Library.Single().DownloadPath = "";
            f.Settings.Library.Single().ContentPath = f.Root; f.Store.Save(f.Settings);
            Check(f.Restart().Downloads.Single().State == "Installed — original removed" && f.Restart().Library.Count == 1, "Completed evidence never replays installer source deletion");
        }
        using (var f = new Fixture("Direct"))
        {
            var legacy = new DownloadJob { State = "Downloaded", Destination = f.Job.StagingPath, DownloadedBytes = f.Bytes.Length };
            f.Store.Save(new() { Downloads = [legacy], Library = [new() { DownloadPath = legacy.Destination, ContentPath = legacy.Destination }] });
            Check(f.Restart().Downloads.Single().State == "Downloaded" && f.Restart().Library.Single().DownloadArtifacts.Count == 0, "Legacy completed files preserve their existing records without invented evidence");
        }
        Console.WriteLine("PASS actual JSON/filesystem crash boundaries, save faults, collisions, full hashes, idempotency, cancellation, A2/A3 ownership and legacy completion");
    }

    private static async Task CrashAt(Fixture f, string boundary)
    {
        try { await f.Complete((point, _) => { if (point == boundary) throw new Crash(); }); }
        catch (Crash) { return; }
        throw new Exception("Crash boundary did not fire: " + boundary);
    }
    private static bool EqualBytes(string path, byte[]? bytes) => bytes is null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new Exception("Expected completion failure"); }
    private static void RejectAction(Action action)
    { try { action(); } catch (IOException) { return; } throw new Exception("Expected ownership rejection"); }
}
