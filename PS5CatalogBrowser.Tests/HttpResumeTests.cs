using PS5CatalogBrowser.Core;

internal static class HttpResumeTests
{
    public static async Task RunAsync()
    {
        using var server = new RangeFixtureServer();
        var root = Path.Combine(Path.GetTempPath(), "PS5HttpResume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            async Task<(SettingsStore Store, AppSettings Settings, DownloadJob Job)> Partial(string name, string mode = "good")
            {
                var store = new SettingsStore(Path.Combine(root, name, "settings"));
                var job = new DownloadJob { SourceId = "synthetic", Source = "Local fixture", EntryId = "entry", ArtifactId = name,
                    SelectionIdentity = name, Title = name, Url = server.Url(mode), FileName = "fixture.pkg", Destination = Path.Combine(root, name, "fixture.pkg"),
                    ExpectedSize = server.Bytes.Length, ExpectedSha256 = server.Digest };
                BrowserDownloadFiles.PrepareStaging(job);
                var settings = new AppSettings { Sources = [], Downloads = [job], DownloadRoot = Path.GetDirectoryName(job.Destination)! };
                store.Save(settings);
                using var transfer = new HttpTransfer(); using var stop = new CancellationTokenSource();
                await Reject(() => transfer.DownloadAsync(job, new InlineProgress(j => { if (j.DownloadedBytes >= 64 * 1024) stop.Cancel(); }),
                    stop.Token, _ => store.SaveAsync(settings)));
                return (store, settings, job);
            }

            var first = await Partial("sequential");
            Check(first.Job.State == "Paused" && first.Job.HttpCheckpoint!.Cursor >= 64 * 1024 && first.Job.CanResume, "pause checkpoints written bytes");
            var originalId = first.Job.Id; var originalSelection = first.Job.SelectionIdentity;
            for (var i = 0; i < 2; i++)
            {
                using var transfer = new HttpTransfer(); using var stop = new CancellationTokenSource();
                var goal = first.Job.HttpCheckpoint!.Cursor + 64 * 1024;
                await Reject(() => transfer.DownloadAsync(first.Job, new InlineProgress(j => { if (j.DownloadedBytes >= goal) stop.Cancel(); }),
                    stop.Token, _ => first.Store.SaveAsync(first.Settings), true));
                Check(first.Job.HttpCheckpoint.Cursor >= goal, "sequential pause advances contiguous prefix");
            }
            var requestCount = server.Requests.Count;
            var restarted = first.Store.LoadForStartup(); var resumed = restarted.Downloads.Single();
            Check(server.Requests.Count == requestCount && resumed.State == "Paused" && resumed.CanResume, "startup is local and retains paused state");
            var cursor = resumed.HttpCheckpoint!.Cursor;
            using (var transfer = new HttpTransfer()) await transfer.DownloadAsync(resumed, null, CancellationToken.None, _ => first.Store.SaveAsync(restarted), true);
            await DownloadCompletion.CompleteAsync(resumed, restarted, first.Store.Save, CancellationToken.None);
            await DownloadCompletion.CompleteAsync(resumed, restarted, first.Store.Save, CancellationToken.None);
            Check(resumed.Id == originalId && resumed.SelectionIdentity == originalSelection && restarted.Library.Count == 1 &&
                File.ReadAllBytes(resumed.Destination).SequenceEqual(server.Bytes) && resumed.ActualSha256 == server.Digest, "restart preserves identity, exact bytes and one A4 registration");
            Check(server.Requests.Any(r => r.Range == $"bytes={cursor}-" && r.IfRange == RangeFixtureServer.ETag), "range and If-Range are sent");
            resumed.CleanupPending = true;
            await Reject(() => Task.Run(() => HttpResume.DiscardPartial(resumed)));
            Check(File.Exists(resumed.Destination), "completed file cannot be discarded");

            foreach (var mode in new[] { "ignored", "start", "total", "length", "412", "416", "etag", "weak-resume", "missing-resume", "short", "excess", "html", "redirect-unsafe" })
            {
                var test = await Partial("range-" + mode, mode);
                var before = test.Job.HttpCheckpoint!.Cursor;
                using var transfer = new HttpTransfer();
                await Reject(() => transfer.DownloadAsync(test.Job, null, CancellationToken.None, _ => test.Store.SaveAsync(test.Settings), true));
                Check(!File.Exists(test.Job.Destination) && test.Settings.Library.Count == 0 && test.Job.HttpCheckpoint.Cursor == before &&
                    File.Exists(test.Job.StagingPath), "invalid range preserves checkpoint without promotion: " + mode);
                if (mode != "redirect-unsafe") Check(!test.Job.CanResume && test.Job.HttpCheckpoint.RestartRequired, "protocol failure requires restart: " + mode);
            }
            foreach (var mode in new[] { "weak", "missing", "unknown" })
            {
                var test = await Partial("nonresume-" + mode, mode);
                Check(!test.Job.CanPause && !test.Job.CanResume && !HttpResume.Eligible(test.Job), "non-resumable controls: " + mode);
                using var transfer = new HttpTransfer();
                await Reject(() => transfer.DownloadAsync(test.Job, null, CancellationToken.None, _ => test.Store.SaveAsync(test.Settings), true));
            }
            var noLength = await Partial("resume-unknown", "resume-unknown");
            using (var transfer = new HttpTransfer()) await transfer.DownloadAsync(noLength.Job, null, CancellationToken.None, _ => noLength.Store.SaveAsync(noLength.Settings), true);
            Check(File.ReadAllBytes(noLength.Job.StagingPath).SequenceEqual(server.Bytes), "range without Content-Length is bounded by Content-Range and EOF");

            var redirected = await Partial("redirected", "redirect-good");
            using (var transfer = new HttpTransfer()) await transfer.DownloadAsync(redirected.Job, null, CancellationToken.None, _ => redirected.Store.SaveAsync(redirected.Settings), true);
            Check(redirected.Job.Url == server.Url("redirect-good") && !File.ReadAllText(redirected.Store.FilePath).Contains("DO_NOT_PERSIST") &&
                File.ReadAllBytes(redirected.Job.StagingPath).SequenceEqual(server.Bytes), "redirected conditional transfer retains only original URL");

            // Additional local/crash/ownership cases below use the same isolated settings and fixture.
            await LocalAndCrashCasesAsync(root, server, Partial);
            await CheckpointCadenceAsync(root);
            Console.WriteLine("PASS HTTP resume: protocol, sequential pause/restart, exact bytes, checkpoints, save faults, identity, links, ownership and A4 registration");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task LocalAndCrashCasesAsync(string root, RangeFixtureServer server,
        Func<string, string, Task<(SettingsStore Store, AppSettings Settings, DownloadJob Job)>> partial)
    {
        foreach (var kind in new[] { "short", "long", "missing", "path", "owner", "selection", "size", "validator", "file-id", "prefix", "junction" })
        {
            var test = await partial("local-" + kind, "good"); var job = test.Job;
            var initial = job.HttpCheckpoint!.Cursor;
            string? junction = null;
            switch (kind)
            {
                case "short": using (var file = File.OpenWrite(job.StagingPath)) file.SetLength(initial - 1); break;
                case "long": using (var file = new FileStream(job.StagingPath, FileMode.Append)) file.Write("uncheckpointed"u8); break;
                case "missing": File.Delete(job.StagingPath); break;
                case "path": job.StagingPath = Path.Combine(root, "unowned.part"); File.WriteAllText(job.StagingPath, "user bytes"); break;
                case "owner": File.WriteAllText(Path.Combine(Path.GetDirectoryName(job.StagingPath)!, ".owner"), "wrong owner"); break;
                case "selection": job.ArtifactId = "refreshed artifact"; break;
                case "size": job.ExpectedSize++; break;
                case "validator": job.HttpCheckpoint.StrongETag = "\"other\""; break;
                case "file-id": File.Move(job.StagingPath, job.StagingPath + ".original"); File.WriteAllBytes(job.StagingPath, server.Bytes[..(int)initial]); break;
                case "prefix": using (var file = File.OpenWrite(job.StagingPath)) file.WriteByte((byte)(server.Bytes[0] ^ 255)); break;
                case "junction":
                    junction = Path.GetDirectoryName(job.StagingPath)!; var original = junction + ".original"; Directory.Move(junction, original);
                    using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{original}\"")
                        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
                    { process!.WaitForExit(); Check(process.ExitCode == 0, "synthetic junction created"); } break;
            }
            test.Store.Save(test.Settings);
            var requests = server.Requests.Count;
            var loaded = test.Store.LoadForStartup(); var saved = loaded.Downloads.Single();
            using var transfer = new HttpTransfer();
            if (kind == "long")
            {
                Check(new FileInfo(saved.StagingPath).Length > initial && saved.CanResume, "startup preserves trailing bytes");
                await transfer.DownloadAsync(saved, null, CancellationToken.None, _ => test.Store.SaveAsync(loaded), true);
                Check(File.ReadAllBytes(saved.StagingPath).SequenceEqual(server.Bytes), "explicit resume truncates only verified trailing bytes");
            }
            else
            {
                await Reject(() => transfer.DownloadAsync(saved, null, CancellationToken.None, _ => test.Store.SaveAsync(loaded), true));
                Check(server.Requests.Count == requests && !File.Exists(saved.Destination) && loaded.Library.Count == 0, "unsafe local state never contacts provider: " + kind);
                if (kind == "file-id")
                {
                    saved.CleanupPending = true;
                    await Reject(() => Task.Run(() => HttpResume.DiscardPartial(saved)));
                    Check(File.Exists(saved.StagingPath), "changed identity cannot be discarded");
                }
            }
            if (junction is not null) Directory.Delete(junction);
        }

        var legacy = new DownloadJob { State = "Downloading", Destination = Path.Combine(root, "legacy.pkg"), Url = server.Url("good") };
        BrowserDownloadFiles.PrepareStaging(legacy); File.WriteAllText(legacy.StagingPath, "legacy bytes");
        var legacyStore = new SettingsStore(Path.Combine(root, "legacy-settings")); legacyStore.Save(new() { Downloads = [legacy] });
        Check(!legacyStore.LoadForStartup().Downloads.Single().CanResume && File.Exists(legacy.StagingPath), "legacy partial has no invented identity");

        var firstCheckpoint = new DownloadJob { State = "Downloading", SelectionIdentity = "first-checkpoint", SourceId = "synthetic", EntryId = "entry", ArtifactId = "first-checkpoint",
            Destination = Path.Combine(root, "first-checkpoint", "fixture.pkg"), FileName = "fixture.pkg", Url = server.Url("good"), ExpectedSize = server.Bytes.Length };
        BrowserDownloadFiles.PrepareStaging(firstCheckpoint);
        var firstStore = new SettingsStore(Path.Combine(root, "first-checkpoint", "settings")); var firstSettings = new AppSettings { Sources = [], Downloads = [firstCheckpoint] }; firstStore.Save(firstSettings);
        using (var transfer = new HttpTransfer())
            await Reject(() => transfer.DownloadAsync(firstCheckpoint, new InlineProgress(j => { if (j.DownloadedBytes >= 64 * 1024) throw new IOException("crash before first byte cursor save"); }),
                CancellationToken.None, _ => firstStore.SaveAsync(firstSettings)));
        var beforeFirst = firstStore.LoadForStartup().Downloads.Single();
        Check(beforeFirst.HttpCheckpoint!.Cursor == 0 && !beforeFirst.CanResume && beforeFirst.State == "Needs attention" && new FileInfo(beforeFirst.StagingPath).Length >= 64 * 1024,
            "crash before first byte checkpoint keeps trailing data without inventing a resumable cursor");

        foreach (var boundary in new[] { "BeforeInitialSave", "AfterFlush", "AfterCheckpoint" })
        {
            var test = await partial("crash-" + boundary, "good"); var savedCursor = test.Job.HttpCheckpoint!.Cursor;
            using var transfer = new HttpTransfer(); using var stop = new CancellationTokenSource();
            if (boundary == "BeforeInitialSave")
            {
                test.Job.CleanupPending = true; HttpResume.DiscardPartial(test.Job); test.Job.CleanupPending = false; test.Job.HttpCheckpoint = null;
                test.Job.StagingPath = test.Job.StagingRoot = test.Job.StagingOwner = ""; test.Job.DownloadedBytes = 0;
                BrowserDownloadFiles.PrepareStaging(test.Job); test.Store.Save(test.Settings);
                await Reject(() => transfer.DownloadAsync(test.Job, null, stop.Token, _ => throw new IOException("crash before checkpoint")));
                var loaded = test.Store.Load();
                Check(loaded.Downloads.Single().HttpCheckpoint is null && !loaded.Downloads.Single().CanResume && File.Exists(test.Job.StagingPath), "crash before checkpoint retains untrusted data");
                continue;
            }
            await Reject(() => transfer.DownloadAsync(test.Job, new InlineProgress(j => { if (j.DownloadedBytes >= savedCursor + 64 * 1024) stop.Cancel(); }), stop.Token,
                _ => test.Store.SaveAsync(test.Settings), true, (point, job) => { if (point == boundary && job.DownloadedBytes > savedCursor) throw new IOException("synthetic crash"); }));
            var recovery = test.Store.LoadForStartup(); var recovered = recovery.Downloads.Single();
            Check(recovered.HttpCheckpoint!.Cursor == (boundary == "AfterFlush" ? savedCursor : test.Job.DownloadedBytes), "crash cursor conservative: " + boundary);
            await transfer.DownloadAsync(recovered, null, CancellationToken.None, _ => test.Store.SaveAsync(recovery), true);
            Check(File.ReadAllBytes(recovered.StagingPath).SequenceEqual(server.Bytes), "crash recovery byte equality: " + boundary);
        }

        foreach (var boundary in new[] { "AfterFlush", "AfterCheckpoint" })
        {
            var test = await partial("process-crash-" + boundary, "good"); var originalCursor = test.Job.HttpCheckpoint!.Cursor;
            var command = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                command.ArgumentList.Add(typeof(HttpResumeTests).Assembly.Location);
            command.ArgumentList.Add("--http-resume-crash"); command.ArgumentList.Add(test.Store.DirectoryPath); command.ArgumentList.Add(boundary);
            using var process = System.Diagnostics.Process.Start(command)!;
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { process.Kill(true); throw; }
            Check(process.ExitCode == 91, "isolated worker exits without unwinding at " + boundary + ": " + await process.StandardError.ReadToEndAsync());
            using var ownership = test.Store.AcquireApplicationOwnership();
            var recovered = test.Store.LoadForStartup(); var job = recovered.Downloads.Single();
            var diskLength = new FileInfo(job.StagingPath).Length;
            Check((boundary == "AfterFlush" ? job.HttpCheckpoint!.Cursor == originalCursor && diskLength > originalCursor : job.HttpCheckpoint!.Cursor > originalCursor && diskLength == job.HttpCheckpoint.Cursor) &&
                job.CanResume, "OS process crash retains conservative checkpoint: " + boundary);
            using var transfer = new HttpTransfer();
            await transfer.DownloadAsync(job, null, CancellationToken.None, _ => test.Store.SaveAsync(recovered), true);
            Check(File.ReadAllBytes(job.StagingPath).SequenceEqual(server.Bytes), "real process-crash restart resumes exact bytes: " + boundary);
        }

        var fault = await partial("save-fault", "good"); var oldCursor = fault.Job.HttpCheckpoint!.Cursor;
        using (var transfer = new HttpTransfer())
        using (var stop = new CancellationTokenSource())
        {
            fault.Store.BeforeSave = _ => throw new IOException("injected settings failure");
            await Reject(() => transfer.DownloadAsync(fault.Job, new InlineProgress(j => { if (j.DownloadedBytes >= oldCursor + 64 * 1024) stop.Cancel(); }), stop.Token,
                _ => fault.Store.SaveAsync(fault.Settings), true));
            Check(fault.Job.HttpCheckpoint.Cursor == oldCursor && fault.Store.Load().Downloads.Single().HttpCheckpoint!.Cursor == oldCursor, "failed save never advances cursor");
            fault.Store.BeforeSave = null;
            await transfer.DownloadAsync(fault.Job, null, CancellationToken.None, _ => fault.Store.SaveAsync(fault.Settings), true);
            Check(File.ReadAllBytes(fault.Job.StagingPath).SequenceEqual(server.Bytes), "retry after save failure uses old prefix");
        }
        var race = await partial("race", "good");
        using (var transfer = new HttpTransfer())
        using (var stop = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = transfer.DownloadAsync(race.Job, new InlineProgress(j => { if (j.DownloadedBytes >= 128 * 1024) stop.Cancel(); }), stop.Token,
                async _ => { entered.TrySetResult(); await unblock.Task; await race.Store.SaveAsync(race.Settings); }, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Reject(() => transfer.DownloadAsync(race.Job, null, CancellationToken.None, _ => race.Store.SaveAsync(race.Settings), true));
            unblock.SetResult(); await Reject(() => running);
            Check(race.Job.HttpCheckpoint!.Cursor >= 128 * 1024, "one competing resume writer wins");
        }
        var owned = await partial("admission", "good");
        var admission = new DownloadAdmission(); admission.RestoreCompletions([owned.Job]); admission.Release(owned.Job);
        Check(admission.Owns(owned.Job), "pause retains admission after startup/release");
        await Reject(() => Task.Run(() => BrowserDownloadFiles.DiscardStaging(owned.Job)));
        Check(File.Exists(owned.Job.StagingPath), "A3 cannot accidentally remove paused partial");
        owned.Job.CleanupPending = true; HttpResume.DiscardPartial(owned.Job);
        Check(!File.Exists(owned.Job.StagingPath), "explicit discard removes exact partial");

        foreach (var scenario in new[] { "correct", "checksum", "catalogue", "same-size-change" })
        {
            var test = await partial("integrity-" + scenario, scenario == "same-size-change" ? "etag" : "good");
            // Model original expectations before resume, with a consistent local binding.
            if (scenario == "checksum") { test.Job.ExpectedSha256 = new string('0', 64); test.Job.HttpCheckpoint!.Identity = HttpResume.Identity(test.Job, test.Job.HttpCheckpoint); }
            if (scenario == "catalogue") { test.Job.ExpectedSize++; test.Job.HttpCheckpoint!.Identity = HttpResume.Identity(test.Job, test.Job.HttpCheckpoint); }
            using var transfer = new HttpTransfer();
            if (scenario == "correct")
            {
                await transfer.DownloadAsync(test.Job, null, CancellationToken.None, _ => test.Store.SaveAsync(test.Settings), true);
                await DownloadCompletion.CompleteAsync(test.Job, test.Settings, test.Store.Save, CancellationToken.None);
                Check(test.Settings.Library.Count == 1 && File.ReadAllBytes(test.Job.Destination).SequenceEqual(server.Bytes), "expected hash matches resumed fixture");
            }
            else
            {
                await Reject(() => transfer.DownloadAsync(test.Job, null, CancellationToken.None, _ => test.Store.SaveAsync(test.Settings), true));
                Check(!File.Exists(test.Job.Destination) && test.Store.Load().Library.Count == 0, "failed integrity cannot publish/register: " + scenario);
            }
        }

        var finalising = await partial("a4-precedence", "good");
        using (var transfer = new HttpTransfer()) await transfer.DownloadAsync(finalising.Job, null, CancellationToken.None,
            _ => finalising.Store.SaveAsync(finalising.Settings), true);
        await Reject(() => DownloadCompletion.CompleteAsync(finalising.Job, finalising.Settings, finalising.Store.Save, CancellationToken.None,
            (point, _) => { if (point == "AfterMove") throw new IOException("crash during A4 promotion"); }));
        var beforeRecovery = server.Requests.Count;
        var recoveredCompletion = finalising.Store.LoadForStartup(); var completed = recoveredCompletion.Downloads.Single();
        Check(server.Requests.Count == beforeRecovery && completed.State == "Downloaded" && completed.Completion?.Phase == "Completed" &&
            !completed.CanResume && recoveredCompletion.Library.Count == 1 && File.ReadAllBytes(completed.Destination).SequenceEqual(server.Bytes), "A4 takes precedence over HTTP resume after promotion crash");
        using (var transfer = new HttpTransfer())
            await Reject(() => transfer.DownloadAsync(completed, null, CancellationToken.None, _ => finalising.Store.SaveAsync(recoveredCompletion), true));
        Check(completed.State == "Downloaded", "HTTP refusal cannot mutate completed A4 state");

        foreach (var kind in new[] { "replaced", "modified" })
        {
            var changed = await partial("completion-" + kind, "good");
            using var transfer = new HttpTransfer();
            await transfer.DownloadAsync(changed.Job, null, CancellationToken.None, _ => changed.Store.SaveAsync(changed.Settings), true);
            if (kind == "replaced")
            { File.Move(changed.Job.StagingPath, changed.Job.StagingPath + ".original"); File.WriteAllBytes(changed.Job.StagingPath, server.Bytes); }
            else using (var file = File.OpenWrite(changed.Job.StagingPath)) file.WriteByte((byte)(server.Bytes[0] ^ 255));
            await Reject(() => DownloadCompletion.CompleteAsync(changed.Job, changed.Settings, changed.Store.Save, CancellationToken.None));
            Check(File.Exists(changed.Job.StagingPath) && !File.Exists(changed.Job.Destination) && changed.Store.Load().Library.Count == 0,
                "HTTP to A4 handoff rejects changed identity/content: " + kind);
        }

        using (var transfer = new HttpTransfer())
            await Reject(() => transfer.DownloadAsync(legacyStore.Load().Downloads.Single(), null, CancellationToken.None, _ => Task.CompletedTask, true));
        Check(File.Exists(legacy.StagingPath), "failed legacy resume cannot remove original partial");
    }

    private static async Task CheckpointCadenceAsync(string root)
    {
        using var server = new RangeFixtureServer(10 * 1024 * 1024, 0); using var transfer = new HttpTransfer();
        var job = new DownloadJob { SourceId = "fixture", EntryId = "large", ArtifactId = "large", SelectionIdentity = "large",
            Url = server.Url("good"), Destination = Path.Combine(root, "cadence", "fixture.pkg"), FileName = "fixture.pkg", ExpectedSize = server.Bytes.Length, ExpectedSha256 = server.Digest };
        BrowserDownloadFiles.PrepareStaging(job);
        var store = new SettingsStore(Path.Combine(root, "cadence", "settings")); var settings = new AppSettings { Downloads = [job], Sources = [] }; store.Save(settings);
        var cursors = new List<long>();
        store.BeforeSave = candidate =>
        {
            var cursor = candidate.Downloads.Single().HttpCheckpoint!.Cursor;
            Check(cursor <= new FileInfo(job.StagingPath).Length, "cursor never exceeds flushed file length"); cursors.Add(cursor);
        };
        using var stop = new CancellationTokenSource();
        await Reject(() => transfer.DownloadAsync(job, new InlineProgress(j => { if (j.DownloadedBytes >= 9 * 1024 * 1024) stop.Cancel(); }), stop.Token, _ => store.SaveAsync(settings)));
        Check(cursors.Count is >= 3 and < 10 && cursors[0] == 0 && cursors.Any(n => n >= 8 * 1024 * 1024 && n < job.DownloadedBytes), "bounded in-flight checkpoint cadence, not per network buffer");
        var loaded = store.LoadForStartup(); job = loaded.Downloads.Single();
        await transfer.DownloadAsync(job, null, CancellationToken.None, _ => store.SaveAsync(loaded), true);
        Check(File.ReadAllBytes(job.StagingPath).SequenceEqual(server.Bytes), "multi-checkpoint large fixture resumes byte-for-byte");
    }

    public static async Task<int> CrashWorkerAsync(string settingsDirectory, string boundary)
    {
        var store = new SettingsStore(settingsDirectory); using var ownership = store.AcquireApplicationOwnership();
        var settings = store.LoadForStartup(); var job = settings.Downloads.Single(); var original = job.HttpCheckpoint!.Cursor;
        job.State = "Downloading"; store.Save(settings);
        using var transfer = new HttpTransfer(); using var stop = new CancellationTokenSource();
        await transfer.DownloadAsync(job, new InlineProgress(j => { if (j.DownloadedBytes >= original + 64 * 1024) stop.Cancel(); }), stop.Token,
            _ => store.SaveAsync(settings), true, (point, current) => { if (point == boundary && current.DownloadedBytes > original) Environment.Exit(91); });
        return 99;
    }
    private sealed class InlineProgress(Action<DownloadJob> report) : IProgress<DownloadJob> { public void Report(DownloadJob value) => report(value); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
}
