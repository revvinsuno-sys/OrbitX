using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && ((Path.GetFileName(Environment.ProcessPath) == "SharpEmu.exe" && Path.GetFileName(Path.GetDirectoryName(args[0])!).StartsWith("dual-fixture-")) ||
            (Path.GetFileName(Environment.ProcessPath) == "kyty_emulator.exe" && args.Length >= 2 && Path.GetFileName(args[1]).StartsWith("dual-fixture-"))))
        {
            Console.WriteLine("DUAL_ARGV=" + System.Text.Json.JsonSerializer.Serialize(args));
            Thread.Sleep(150); return 0;
        }
        if (args.Length >= 2 && args[0] == "--game" && Path.GetFileName(Environment.ProcessPath) == "kyty_emulator.exe" &&
            Path.GetFileName(args[1]).StartsWith("argument-fixture-", StringComparison.Ordinal))
        {
            Console.WriteLine("ACTUAL_ARGV=" + System.Text.Json.JsonSerializer.Serialize(args));
            return args.Contains("--no-splash") ? 17 : 0;
        }
        if (args.Length == 2 && args[0] == "--game" && Path.GetFileName(Environment.ProcessPath) == "kyty_emulator.exe" &&
            Path.GetFileName(args[1]).StartsWith("controller-return-", StringComparison.Ordinal))
        {
            Thread.Sleep(500);
            Console.WriteLine("Synthetic controller return worker");
            return File.ReadAllBytes(args[1])[^1];
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            if (args.Length == 0 || args.Contains("--igdb-credentials"))
            {
                try { IgdbCredentialIsolationTests.Run(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL IGDB isolation: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--igdb-credentials")) { app.Shutdown(0); return; }
            }
            if (args.Length == 2 && args[0] == "--settings-restart")
            {
                try { SettingsNavigationTests.VerifyRestart(args[1]); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL settings restart: " + ex.Message); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--settings-paths"))
            {
                try { await SettingsPathsTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL settings paths: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--orbit-management"))
            {
                try { await OrbitFrontendTests.ManagedActionsAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit management: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--sharpemu"))
            {
                try { await SharpEmuUiTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL SharpEmu UI: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--installed-inventory"))
            {
                try { await InstalledInventoryUiTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Installed inventory UI: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--installed-identity"))
            {
                try { await InstalledIdentityTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL installed identity: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--download-navigation"))
            {
                try { await DownloadNavigationTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL download navigation: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--download-inventory"))
            {
                try { await DownloadMetadataTests.AuditAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL download inventory: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--download-metadata"))
            {
                try { await DownloadMetadataTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL download metadata: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--library-preview"))
            {
                try { await CoverGridTests.RunAsync(previewOnly: true); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL library preview: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Length == 0 || args.Contains("--kyty-update"))
            {
                try { await KytyUpdateUiTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Kyty update UI: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--kyty-update")) { app.Shutdown(0); return; }
            }
            if (args.Contains("--branding-startup"))
            {
                try { await BrandingStartupTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL branding/startup: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Length == 0 || args.Contains("--settings-navigation"))
            {
                try { await SettingsNavigationTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Settings navigation: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--settings-navigation")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--arguments"))
            {
                try { await KytyArgumentsUiTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Kyty arguments UI: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--arguments")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--cover-grid"))
            {
                try { await CoverGridTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL cover grid: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--cover-grid")) { app.Shutdown(0); return; }
            }
            if (args.Contains("--game-return"))
            {
                try { await GameReturnTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL game return: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--library-cards"))
            {
                try { await LibraryCardTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL library cards: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--compatibility"))
            {
                try { await CompatibilityTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL compatibility: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--package-folders"))
            {
                try { await PackageFolderTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL package folders: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--download-retry"))
            {
                try { await DownloadRetryTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL download retry: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--bandwidth"))
            {
                try { await DownloadBandwidthTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--installation-lifecycle"))
            {
                try { await InstallationLifecycleTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL installation lifecycle: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--installation-queue"))
            {
                try { await InstallationQueueTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL installation queue: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--download-management"))
            {
                try { await DownloadManagementTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL download management: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--akirabox"))
            {
                try
                {
                    await AkiraBoxFlowTests.RunAsync();
                    app.Shutdown(0);
                }
                catch (Exception ex) { Console.Error.WriteLine("FAIL AkiraBox flow: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--orbit-install-e2e"))
            {
                try { await OrbitInstallationFlowTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit installation end-to-end: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--browser-downloads"))
            {
                try { await RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL browser downloads: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Contains("--orbit-frontend-live"))
            {
                try { await OrbitFrontendLiveReadTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL live Orbit frontend: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Length == 0 || args.Contains("--orbit-frontend"))
            {
                try { await OrbitFrontendTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit frontend: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--orbit-frontend")) { app.Shutdown(0); return; }
            }
            if (args.Contains("--runnable-content"))
            {
                try { await RunnableContentUiTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL content installation UI: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Length == 0 || args.Contains("--provider-flow"))
            {
                try { await ProviderFlowTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL provider flow: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--provider-flow")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--source-visibility"))
            {
                try { await SourceVisibilityTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL source visibility: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--source-visibility")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--presentation"))
            {
                try { await CatalogPresentationUiTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL catalogue presentation: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--presentation")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--orbit-keys"))
            {
                try { await OrbitKeyUiTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit key UI: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--orbit-keys")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--orbit-live"))
            {
                try { await LiveOrbitIntegrationTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL live Orbit integration: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--orbit-live")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--library-deletion-ui"))
            {
                try { await LibraryDeletionUiTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL library deletion UI: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--library-deletion-ui")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--installation-history") || args.Contains("--installation-ui"))
            {
                try { await InstallationHistoryTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL installation history: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--installation-history")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--http-resume"))
            {
                try { await HttpResumeIntegrationTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL HTTP resume UI: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--http-resume")) { app.Shutdown(0); return; }
            }
            if (args.Length == 0 || args.Contains("--orbit-downloads"))
            {
                try { await OrbitDownloadIntegrationTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit downloads: " + ex); app.Shutdown(1); return; }
                if (args.Contains("--orbit-downloads")) { app.Shutdown(0); return; }
            }
            if (args.Contains("--orbit") || args.Contains("--orbit-smoke"))
            {
                try { await LocalOrbitIntegrationTests.RunAsync(args); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL local Orbit integration: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Length == 0)
            {
                try { await LocalOrbitIntegrationTests.RunAsync(args); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL local Orbit integration: " + ex); app.Shutdown(1); return; }
            }
            if (args.Contains("--ownership"))
            {
                try { await NativeOwnershipTests.RunAsync(); app.Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL native ownership: " + ex); app.Shutdown(1); }
                return;
            }
            if (args.Length == 0)
            {
                try { await NativeOwnershipTests.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL native ownership: " + ex); app.Shutdown(1); return; }
            }
            try { if (args.Contains("--interactive")) await InteractiveAsync(); else if (args.Contains("--installation-ui")) await InstallationUiAsync(); else if (args.Contains("--admission")) await AdmissionAsync(); else if (args.Contains("--direct-downloads")) await DirectDownloadsAsync(); else if (args.Contains("--cleanup")) await CleanupAsync(); else if (args.Contains("--completion")) await CompletionRecoveryAsync(); else { await CompletionRecoveryAsync(); await CleanupAsync(); await AdmissionAsync(); await InstallationUiAsync(); await DirectDownloadsAsync(); await RunAsync(); } app.Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine("FAIL browser integration: " + ex); app.Shutdown(1); }
        });
        return app.Run();
    }
    private static async Task CompletionRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5RecoveryUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        BrowserDownloadWindow? window = null;
        try
        {
            foreach (var method in new[] { "Direct", "Browser", "External import" })
            foreach (var phase in new[] { "Pending", "Promoted", "Completed" })
            {
                var store = new SettingsStore(Path.Combine(root, method + phase, "settings"));
                store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, method + phase, "downloads") });
                var artifact = new CatalogArtifact { Id = "selected", Url = server.Url("/file"), FileName = "fixture.pkg", Size = server.Bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(server.Bytes)).ToLowerInvariant() };
                var entry = new CatalogEntry { SourceId = "local", Id = "local:entry", Title = method + phase, SourceName = "Local fixture", Artifacts = [artifact] };
                var mirror = new DownloadMirror { Artifact = artifact, Url = server.Url("/landing?target=/redirect"), ExpectedFileName = "fixture.pkg" };
                var fired = false;
                store.BeforeSave = candidate =>
                {
                    if (!fired && candidate.Downloads.SingleOrDefault()?.Completion?.Phase == phase)
                    { fired = true; throw new IOException("Injected " + phase + " persistence failure"); }
                };
                string destination;
                using (var model = new MainViewModel(store))
                {
                    if (method == "Direct") await model.DownloadAsync(entry, artifact);
                    else if (method == "Browser")
                    {
                        window = new(entry, mirror, model.BrowserDownloads, Path.Combine(root, "profile"), _ => true);
                        window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                        await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
                        await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
                        await WaitAsync(() => Task.FromResult(model.Downloads.Count == 1 && model.Downloads.Single().State == "Needs attention" && !model.BrowserDownloads.HasActive));
                        window.Close(); window = null;
                    }
                    else
                    {
                        var external = Path.Combine(root, "external.pkg"); File.WriteAllBytes(external, server.Bytes);
                        try { await model.BrowserDownloads.ImportAsync(entry, mirror, external); throw new Exception("Expected registration failure"); }
                        catch (IOException) { }
                        Check(File.ReadAllBytes(external).SequenceEqual(server.Bytes), "External import recovery preserves original source");
                    }
                    var job = model.Downloads.Single(); destination = job.Destination; model.SelectedJob = job;
                    Check(fired && job.State == "Needs attention" && job.Error.Length > 0 && model.Library.Count == 0, "WPF exposes unresolved persistence failure without an unsaved library entry");
                    try { model.ClearSelectedHistory(); throw new Exception("Clear discarded recovery evidence"); } catch (IOException) { }
                    try { await model.DiscardSelectedDownloadAsync(); throw new Exception("Discard deleted verified staging"); } catch (IOException) { }
                    await model.ClearAllDownloadsAsync(); model.CancelSelectedDownload();
                    Check(model.Downloads.Count == 1 && job.State == "Needs attention", "Clear all and cancel retain unresolved completion evidence");
                    try { await model.DownloadAsync(entry, artifact); throw new Exception("Duplicate admission accepted during recovery"); }
                    catch (IOException ex) when (ex.Message.StartsWith("This item already has an active download.")) { }
                    Check(!File.ReadAllText(store.FilePath).Contains("temporary-secret"), "Native capability URL never enters completion evidence");
                }
                store.BeforeSave = null;
                using (var recovered = new MainViewModel(store))
                {
                    Check(recovered.Downloads.Single().State == (method == "External import" ? "Imported" : "Downloaded") && recovered.Library.Count == 1,
                        "WPF startup safely reconciles " + method + " " + phase);
                    Check(File.ReadAllBytes(destination).SequenceEqual(server.Bytes) && recovered.Library.Single().DownloadArtifacts.Single().ArtifactId == artifact.Id,
                        "Recovery preserves selected artifact and real file bytes");
                    // Successful durable reconciliation releases the original A2 owner.
                    await recovered.DownloadAsync(entry, artifact);
                    Check(recovered.Downloads.Count == 2 && recovered.Library.Count == 2, "Admission releases only after durable terminal completion");
                }
                using var repeated = new MainViewModel(store);
                Check(repeated.Library.Count == 2 && store.Load().Library.Count == 2, "Repeated WPF startup creates no duplicates");
            }
            // Recovery conflict must restore admission even though no native writer survives restart.
            var conflictStore = new SettingsStore(Path.Combine(root, "conflict", "settings"));
            var conflictEntry = new CatalogEntry { Id = "conflict", SourceId = "local", Title = "Conflict" };
            var conflictArtifact = new CatalogArtifact { Id = "conflict", Url = server.Url("/file") };
            var conflictJob = BrowserDownloadFiles.Prepare(conflictEntry, new() { Artifact = conflictArtifact, Url = conflictArtifact.Url }, Path.Combine(root, "conflict", "downloads"), "fixture.pkg", []);
            conflictJob.SelectionIdentity = DownloadAdmission.Capture(conflictEntry, conflictArtifact); conflictJob.DownloadedBytes = server.Bytes.Length;
            File.WriteAllBytes(conflictJob.StagingPath, server.Bytes);
            var conflictSettings = new AppSettings { Sources = [], Downloads = [conflictJob] };
            try
            {
                await DownloadCompletion.CompleteAsync(conflictJob, conflictSettings, conflictStore.Save, CancellationToken.None,
                    (point, job) => { if (point == "BeforeMove") File.WriteAllText(job.Destination, "unrelated collision"); });
            }
            catch (IOException) { }
            using var conflicted = new MainViewModel(conflictStore);
            conflicted.SelectedJob = conflicted.Downloads.Single();
            Check(conflicted.SelectedJob.State == "Needs attention" && conflicted.Library.Count == 0, "Unsafe recovery remains visible on existing Downloads screen");
            try { await conflicted.DownloadAsync(conflictEntry, conflictArtifact); throw new Exception("Recovery did not restore A2 admission"); }
            catch (IOException ex) when (ex.Message.StartsWith("This item already has an active download.")) { }
            Console.WriteLine("PASS direct/native/import persistence faults, WPF restart recovery, recovery status, history/discard/cancel protection, A2 release and session-secret exclusion");
        }
        finally
        {
            window?.Close();
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) { await Task.Delay(200); }
            }
            if (Directory.Exists(root)) throw new IOException("Recovery fixture remains in use: " + root);
        }
    }

    private static async Task CleanupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5CleanupUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var entry = new CatalogEntry { Id = "fixture", Title = "Cleanup fixture" };
            var mirror = new DownloadMirror { Artifact = new() { Id = "fixture", Url = "https://example.test/file" }, Url = "https://example.test/file" };
            var job = BrowserDownloadFiles.Prepare(entry, mirror, Path.Combine(root, "downloads"), "fixture.pkg", []);
            File.WriteAllText(job.StagingPath, "synthetic incomplete bytes");
            var complete = Path.Combine(root, "complete.pkg"); File.WriteAllText(complete, "completed bytes");
            store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads"), Downloads = [job,
                new() { State = "Downloaded", Destination = complete, DownloadedBytes = new FileInfo(complete).Length }],
                Library = [new() { DownloadPath = complete, ContentPath = complete }] });
            using (var model = new MainViewModel(store))
            {
                model.SelectedJob = model.Downloads.Single(j => j.State == "Downloaded"); model.ClearSelectedHistory();
                Check(File.ReadAllText(complete) == "completed bytes" && store.Load().Library.Count == 1, "Selected history preserves completed bytes and registered library");
                job = model.Downloads.Single(); model.SelectedJob = job;
                await model.ClearAllDownloadsAsync();
                Check(model.Downloads.Contains(job) && File.Exists(job.StagingPath), "Keep and Clear All preserve interrupted staging");
                using (var lockedSettings = new FileStream(store.FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await model.DiscardSelectedDownloadAsync();
                    Check(job.State == "Cleanup failed" && !job.IsCleared && File.Exists(job.StagingPath) && store.Load().Downloads.Any(j => j.Id == job.Id && !j.IsCleared),
                        "Persistence failure before cleanup preserves bytes and recoverable record");
                }
                using (var lockedPartial = new FileStream(job.StagingPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await model.DiscardSelectedDownloadAsync();
                    Check(job.State == "Cleanup failed" && model.Downloads.Contains(job) && File.Exists(job.StagingPath), "Locked partial failure is visible and retryable");
                }
            }
            using (var model = new MainViewModel(store))
            {
                job = model.Downloads.Single(); model.SelectedJob = job;
                Check(job.State == "Cleanup failed" && File.Exists(job.StagingPath), "Cleanup failure survives restart");
                await model.DiscardSelectedDownloadAsync();
                Check(model.Downloads.Count == 0 && !File.Exists(job.StagingPath), "Explicit retry completes filesystem and persisted cleanup");

                var external = Path.Combine(root, "external.pkg");
                using (var file = new FileStream(external, FileMode.CreateNew)) file.SetLength(64 * 1024 * 1024);
                var copying = model.BrowserDownloads.ImportAsync(entry, mirror, external);
                var import = model.Downloads.Single();
                Check(model.BrowserDownloads.IsActive(import), "Copying import is owned by exact job");
                await model.BrowserDownloads.CancelAsync(import); await copying;
                Check(!model.BrowserDownloads.IsActive(import) && import.State == "Interrupted" && File.Exists(import.StagingPath), "Import cancellation waits for copy handles and keeps partial");
                using (var handle = new FileStream(import.StagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Check(new FileInfo(external).Length == 64 * 1024 * 1024, "External source remains untouched");
            }
            using (var model = new MainViewModel(store))
            {
                var import = model.Downloads.Single(); model.SelectedJob = import;
                Check(import.State == "Interrupted" && import.Error.Contains("retained"), "Interrupted import retained after restart");
                var registered = new LocalLibraryEntry { DownloadPath = import.StagingPath, ContentPath = import.StagingPath };
                model.Settings.Library.Add(registered); model.Library.Add(registered);
                await model.DiscardSelectedDownloadAsync();
                Check(model.Downloads.Contains(import) && File.Exists(import.StagingPath) && import.Error.Contains("registered library"), "Registered library path cannot be deleted even if staging ownership matches");
                model.Settings.Library.Remove(registered); model.Library.Remove(registered);
                await model.DiscardSelectedDownloadAsync();
                Check(model.Downloads.Count == 0 && !File.Exists(import.StagingPath), "Interrupted import can be explicitly discarded");
            }
            // Simulate failed persistence after filesystem cleanup: the durable intent identifies the exact missing job.
            var removed = BrowserDownloadFiles.Prepare(entry, mirror, Path.Combine(root, "downloads"), "removed.pkg", []);
            File.WriteAllText(removed.StagingPath, "synthetic bytes"); removed.CleanupPending = true;
            store.Save(new() { Sources = [], Downloads = [removed] }); BrowserDownloadFiles.DiscardStaging(removed);
            using (var lockedSettings = new FileStream(store.FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                removed.IsCleared = true;
                try { store.Save(new() { Downloads = [removed] }); throw new Exception("Expected save failure"); } catch (IOException) { }
            }
            using (var model = new MainViewModel(store))
            {
                model.SelectedJob = model.Downloads.Single();
                Check(model.SelectedJob.CleanupPending && model.SelectedJob.Error.Contains("missing"), "Failed save after deletion reconciles missing partial without hiding the record");
                await model.DiscardSelectedDownloadAsync(); Check(model.Downloads.Count == 0, "Missing owned staging retry safely clears history");
            }
            Console.WriteLine("PASS cleanup UI policy, persisted keep/retry, locked partial, persistence failures and interrupted import writer release");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task AdmissionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5AdmissionBrowser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        BrowserDownloadWindow? first = null, second = null;
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new AppSettings { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
            using var model = new MainViewModel(store);
            var entry = new CatalogEntry { SourceId = "fixture", Id = "entry", Title = "Admission fixture" };
            var artifact = new CatalogArtifact { Id = "selected", Url = server.Url("/slow"), FileName = "fixture.pkg", Mirrors = [server.Url("/file")] };
            var mirror = new DownloadMirror { Artifact = artifact, Url = server.Url("/landing?target=/slow"), Provider = "Local fixture" };
            async Task Reject(Func<Task> action)
            {
                try { await action(); }
                catch (IOException ex) when (ex.Message.StartsWith("This item already has an active download.")) { return; }
                throw new Exception("Duplicate must be rejected.");
            }
            var running = model.DownloadAsync(entry, artifact);
            await Reject(() => model.DownloadAsync(entry, artifact));
            await Reject(() => model.DownloadAsync(entry, artifact, server.Url("/file")));
            var active = model.Downloads.Single();
            Check(store.Load().Downloads.Count == 1, "Repeated clicks persist only one job");
            var queuedArtifact = new CatalogArtifact { Id = "queued", Url = server.Url("/file"), FileName = "queued.pkg" };
            var queued = model.DownloadAsync(entry, queuedArtifact);
            await Reject(() => model.DownloadAsync(entry, queuedArtifact));
            var queuedJob = model.Downloads.Last();
            await model.RestartAsync(queuedJob);
            Check(model.Downloads.Count == 2, "Retry does not duplicate queued owner");
            entry.Artifacts = [new() { Id = "independent", Url = server.Url("/file") }, artifact];
            await Reject(() => model.DownloadAllAsync(entry));
            Check(model.Downloads.Count == 2, "Conflicting batch creates no partial jobs");
            entry.Artifacts = [queuedArtifact, queuedArtifact];
            await Reject(() => model.DownloadAllAsync(entry));
            var external = Path.Combine(root, "external.pkg"); File.WriteAllBytes(external, server.Bytes);
            await Reject(() => model.BrowserDownloads.ImportAsync(entry, mirror, external));
            var profile = Path.Combine(root, "profile");
            first = new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads, profile, _ => true);
            first.Show(); await first.Ready.Task;
            await WaitAsync(async () => await first.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            // The window retains the selection captured when it opened, even if the catalogue object changes.
            artifact.Url = server.Url("/file");
            var nativeRejected = new TaskCompletionSource<bool>();
            first.Browser.CoreWebView2.DownloadStarting += (_, e) => nativeRejected.TrySetResult(e.Cancel);
            await first.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            Check(await nativeRejected.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Direct owner rejects native download using the window's immutable selection");
            artifact.Url = server.Url("/slow");
            Check(model.Downloads.Count == 2 && Directory.GetDirectories(Path.Combine(model.DownloadRoot, ".ps5catalog-staging")).Length == 2,
                "Rejected native download creates no reservation beyond the two admitted direct jobs");
            model.SelectedJob = active; model.CancelSelectedDownload();
            await Reject(() => model.DownloadAsync(entry, artifact));
            await running; await queued;
            Check(active.State == "Cancelled", "Direct cancellation stops writer before release");
            var retryIdentity = active.SelectionIdentity;
            var retry = model.RestartAsync(active);
            await Reject(() => model.DownloadAsync(entry, artifact));
            model.SelectedJob = active; model.CancelSelectedDownload(); await retry;
            Check(active.SelectionIdentity == retryIdentity && model.Downloads.Count == 2, "Retry retains original identity and job");

            first.Close();
            first = new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads, profile, _ => true);
            first.Show(); await first.Ready.Task;
            await WaitAsync(async () => await first.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            await first.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await WaitAsync(() => Task.FromResult(model.Downloads.Count == 3 && model.Downloads.Last().DownloadedBytes > 0));
            var browserJob = model.Downloads.Last(); model.BrowserDownloads.Pause(browserJob);
            await WaitAsync(() => Task.FromResult(browserJob.State == "Paused"));
            await Reject(() => model.DownloadAsync(entry, artifact));
            await Reject(() => model.BrowserDownloads.ImportAsync(entry, mirror, external));
            second = new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads, profile, _ => true);
            second.Show(); await second.Ready.Task;
            await WaitAsync(async () => await second.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            var secondRejected = new TaskCompletionSource<bool>();
            second.Browser.CoreWebView2.DownloadStarting += (_, e) => secondRejected.TrySetResult(e.Cancel);
            await second.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            Check(await secondRejected.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Second browser window rejects same selection");
            Check(model.Downloads.Count == 3 && Directory.GetDirectories(Path.Combine(model.DownloadRoot, ".ps5catalog-staging")).Length == 1, "Paused owner has only one staging directory");
            model.BrowserDownloads.Cancel(browserJob);
            await WaitAsync(() => Task.FromResult(!model.BrowserDownloads.IsActive(browserJob)));
            second.Close(); second = null; first.Close(); first = null;
            await model.BrowserDownloads.ImportAsync(entry, mirror, external);
            Check(model.Downloads.Last().State == "Imported", "Import admitted after native writer termination");

            var count = model.Downloads.Count;
            var retainedStaging = Directory.GetDirectories(Path.Combine(model.DownloadRoot, ".ps5catalog-staging")).Order().ToArray();
            using (var blockedPersistence = new FileStream(store.FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                try { await model.DownloadAsync(entry, queuedArtifact); throw new Exception("Expected persistence failure"); }
                catch (IOException) { }
                Check(model.Downloads.Count == count && model.Settings.Downloads.Count == count, "Persistence failure rolls back visible and persistent lists");
                try { await model.BrowserDownloads.ImportAsync(entry, mirror, external); throw new Exception("Expected import persistence failure"); }
                catch (IOException) { }
                Check(Directory.GetDirectories(Path.Combine(model.DownloadRoot, ".ps5catalog-staging")).Order().SequenceEqual(retainedStaging), "Failed import admission removes its staging and preserves existing partials");
            }
            await model.DownloadAsync(entry, queuedArtifact);
            Check(model.Downloads.Last().State == "Downloaded", "Failed persistence leaves no hidden reservation");
            entry.Artifacts = [new() { Id = "part1", Url = server.Url("/file"), FileName = "same.pkg" }, new() { Id = "part2", Url = server.Url("/file"), FileName = "same.pkg" }];
            await model.DownloadAllAsync(entry);
            Check(model.Downloads.TakeLast(2).All(j => j.State == "Downloaded") && model.Downloads.TakeLast(2).Select(j => j.Destination).Distinct().Count() == 2, "Valid batch preserves distinct part destinations");
            Console.WriteLine("PASS direct/browser/import shared admission, pause, retry, batch and persistence rollback");
        }
        catch (Exception ex) { Console.Error.WriteLine("Admission fixture: " + ex); throw; }
        finally
        {
            second?.Close(); first?.Close();
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) { await Task.Delay(200); }
            }
            if (Directory.Exists(root)) throw new IOException("Admission test profile remains in use: " + root);
        }
    }

    private static async Task DirectDownloadsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5DirectTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        try
        {
            foreach (var (name, path, size, badHash, succeeds) in new (string, string, long?, bool, bool)[]
            {
                ("matching", "/file", server.Bytes.Length, false, true),
                ("header-conflict", "/file", server.Bytes.Length + 1, false, false),
                ("chunked-correct", "/unknown", server.Bytes.Length, false, true),
                ("chunked-short", "/unknown", server.Bytes.Length + 1, false, false),
                ("chunked-excess", "/unknown", server.Bytes.Length - 1, false, false),
                ("unknown", "/unknown", null, false, true),
                ("zero", "/file", 0, false, true),
                ("negative", "/file", -1, false, true),
                ("checksum", "/file", server.Bytes.Length, true, false)
            })
            {
                var store = new SettingsStore(Path.Combine(root, name, "settings"));
                store.Save(new AppSettings { Sources = [], DownloadRoot = Path.Combine(root, name, "downloads") });
                var artifact = new CatalogArtifact { Id = name, FileName = "fixture.pkg", Url = server.Url(path), Size = size,
                    Sha256 = badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(server.Bytes)).ToLowerInvariant() };
                var entry = new CatalogEntry { Id = name, SourceId = "fixture-source", Title = name, Artifacts = [artifact],
                    CoverUrl = server.Url("/cover"), CoverFallbackUrl = server.Url("/cover-fallback") };
                using (var model = new MainViewModel(store))
                {
                    await model.DownloadAsync(entry, artifact);
                    Check(model.Downloads.Single().ExpectedSize == (size is > 0 ? size : null), "Direct job must snapshot only a positive artefact size");
                }
                var saved = store.Load(); var job = saved.Downloads.Single();
                Check(job.CoverUrl == entry.CoverUrl && job.CoverFallbackUrl == entry.CoverFallbackUrl, "Direct admission must persist the selected catalogue artwork");
                Check(saved.Library.Count == (succeeds ? 1 : 0), "Persisted library must contain only validated downloads: " + name);
                Check(File.Exists(job.Destination) == succeeds, "Failed download must never publish a final file: " + name);
                Check(!Directory.GetFiles(Path.GetDirectoryName(job.Destination)!, "*.part").Any(), "Job temporary file must be removed: " + name);
                if (succeeds)
                {
                    Check(job.State == "Downloaded" && job.DownloadedBytes == server.Bytes.Length && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes), "Persisted successful file must match actual fixture bytes");
                    Check(saved.Library.Single().DownloadPath == job.Destination && saved.Library.Single().ContentPath == job.Destination, "Persisted library must reference the validated final file");
                    Check(saved.Library.Single().SourceId == entry.SourceId && saved.Library.Single().CoverUrl == entry.CoverUrl && saved.Library.Single().CoverFallbackUrl == entry.CoverFallbackUrl,
                        "Direct completion must retain catalogue identity and artwork");
                }
                else
                {
                    Check(job.State == (badHash ? "Integrity failed" : "Failed") && job.Error.Length > 0, "Persisted failure must retain its integrity error");
                    if (!badHash) Check(job.Error.Contains($"{size:N0} bytes") && job.Error.Contains($"{server.Bytes.Length:N0} bytes"), "Size error must report expected and actual sizes");
                }
                if (name == "chunked-short")
                {
                    // Reopen the persisted job with a refreshed catalogue that would otherwise accept the response.
                    artifact.Size = server.Bytes.Length;
                    using var reopened = new MainViewModel(store);
                    reopened.Entries.Add(entry);
                    var retry = reopened.Downloads.Single();
                    var otherTemporary = retry.Destination + ".other-job.part"; File.WriteAllText(otherTemporary, "other job");
                    await reopened.RestartAsync(retry);
                    var retried = store.Load();
                    Check(retried.Downloads.Single().Id == job.Id && retried.Downloads.Single().ExpectedSize == size && retry.State == "Failed", "Retry must retain the original persisted size snapshot after catalogue refresh");
                    Check(retried.Library.Count == 0 && !File.Exists(retry.Destination), "Failed retry must never register or publish a file");
                    Check(!File.Exists(retry.Destination + "." + retry.Id + ".part") && File.ReadAllText(otherTemporary) == "other job", "Cleanup must remove only this job's temporary file");
                    // A new job should use the refreshed size while the failed job keeps its snapshot.
                    await reopened.DownloadAsync(entry, artifact);
                    var corrected = store.Load();
                    Check(corrected.Library.Count == 1 && corrected.Downloads.Last().ExpectedSize == artifact.Size && corrected.Downloads.Last().State == "Downloaded", "New job must snapshot refreshed size and register only after validation");
                }
            }
            Console.WriteLine("PASS direct download size, SHA-256, final-file integrity, persisted library and retry snapshot");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task InstallationUiAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5InstallationUi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var startupStore = new SettingsStore(Path.Combine(root, "startup-settings"));
            startupStore.Save(new AppSettings { Sources = [] });
            var mainWindow = new MainWindow(startupStore) { ShowActivated = false };
            try
            {
                mainWindow.Show();
                await mainWindow.Dispatcher.InvokeAsync(() => mainWindow.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ContextIdle);
                var progress = (System.Windows.Controls.ProgressBar)mainWindow.FindName("InstallationProgressBar");
                var binding = System.Windows.Data.BindingOperations.GetBinding(progress, System.Windows.Controls.Primitives.RangeBase.ValueProperty);
                Check(mainWindow.IsVisible && binding?.Mode == System.Windows.Data.BindingMode.OneWay && progress.Value == 0,
                    "Main window must launch with a one-way binding to read-only installation progress");
                Console.WriteLine("PASS WPF main window startup and installation progress binding");
            }
            finally { mainWindow.Close(); }
            var source = Path.Combine(root, "interrupted.exfat"); File.WriteAllText(source, "preserved source fixture");
            var destination = Path.Combine(root, "Installed fixture"); Directory.CreateDirectory(destination);
            var job = new InstallationJob { SourcePath = source, Destination = destination, State = InstallationState.RemovingOriginal, Verified = true, Registered = true };
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new AppSettings { Sources = [], Library = [new LocalLibraryEntry { ContentPath = destination, Classification = "Installed — Ready for launch attempt", InstalledGame = new() { InstallationId = job.Id, Directory = destination, LaunchEligible = true } }] });
            new InstallationJournal(Path.Combine(store.DirectoryPath, "installations")).Save(job);
            using (var model = new MainViewModel(store))
            {
                model.SelectedLibraryEntry = model.Library.Single();
                Check(model.Installations.Single().State == InstallationState.NeedsAttention && !model.CanLaunchSelected && File.Exists(source), "Interrupted installation must preserve source and require reconciliation before launch");
                try { using var competing = new MainViewModel(store); throw new Exception("Second settings writer was admitted"); }
                catch (IOException) { }
            }
            using var reopened = new MainViewModel(store);
            Check(reopened.Installations.Single().State == InstallationState.NeedsAttention && File.Exists(source), "Restart must never automatically retry source deletion");
            Console.WriteLine("PASS WPF persisted interruption, launch gating, source preservation and settings writer ownership");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task InteractiveAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5BrowserTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        var settings = new AppSettings { DownloadRoot = Path.Combine(root, "downloads") };
        var store = new SettingsStore(Path.Combine(root, "settings"));
        using var manager = new BrowserDownloadManager(() => settings.DownloadRoot, () => settings.Downloads,
            job => { settings.Downloads.Add(job); store.Save(settings); }, (job, item) =>
            {
                store.Save(settings); Console.WriteLine($"Fixture download: {job.State}, {job.DownloadedBytes} bytes");
            }, settings, store.Save);
        var mirror = new DownloadMirror { Artifact = new CatalogArtifact { Id = "fixture", Size = server.Bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(server.Bytes)).ToLowerInvariant() },
            Url = server.Url("/landing?target=/file"), ExpectedFileName = "fixture.pkg", Provider = "Local test provider" };
        var window = new BrowserDownloadWindow(new CatalogEntry { Id = "local", Title = "Local browser download smoke test" }, mirror, manager, Path.Combine(root, "profile"));
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult(); window.Show(); await closed.Task;
        Console.WriteLine($"Interactive fixture: {settings.Library.Count} completed library entries");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { Directory.Delete(root, true); return; } catch (IOException) { await Task.Delay(200); } catch (UnauthorizedAccessException) { await Task.Delay(200); }
        }
        Console.WriteLine("Test profile remains in use: " + root);
    }
    private static async Task RunAsync()
    {
        await VerifyArtworkDetachAsync();
        var root = Path.Combine(Path.GetTempPath(), "PS5BrowserTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        var settings = new AppSettings { DownloadRoot = Path.Combine(root, "downloads") };
        var store = new SettingsStore(Path.Combine(root, "settings"));
        using var manager = new BrowserDownloadManager(() => settings.DownloadRoot, () => settings.Downloads,
            job => { settings.Downloads.Add(job); store.Save(settings); },
            (job, item) => store.Save(settings), settings, store.Save);
        var entry = new CatalogEntry { Id = "fixture", Title = "Local browser fixture", SourceName = "Local tests" };
        var artifact = new CatalogArtifact { Id = "file", FileName = "fixture.pkg", Size = server.Bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(server.Bytes)).ToLowerInvariant() };
        var profile = Path.Combine(root, "profile");
        BrowserDownloadWindow? window = null;
        try
        {
            foreach (var endpoint in new[] { "/file", "/redirect", "/unknown" })
            {
                var mirror = new DownloadMirror { Artifact = artifact, Url = server.Url("/landing?target=" + endpoint), Provider = "Local fixture", ExpectedFileName = "fixture.pkg" };
                var prompts = 0;
                window = new BrowserDownloadWindow(entry, mirror, manager, profile, _ => { prompts++; return true; });
                window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
                Check(settings.Downloads.Count == (endpoint == "/file" ? 0 : endpoint == "/redirect" ? 1 : 2), "Landing page must not download automatically");
                var previousCount = settings.Downloads.Count;
                await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
                await WaitAsync(() => Task.FromResult(settings.Downloads.Count == previousCount + 1 && settings.Downloads.Last().State == "Downloaded"));
                var job = settings.Downloads.Last();
                Check(prompts == 1 && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes), "Confirmed download bytes");
                Check(job.DownloadedBytes == server.Bytes.Length && job.BytesPerSecond > 0 && job.ActualSha256 == artifact.Sha256, "Native progress and checksum");
                Check(new LaunchVisibilityConverter().Convert(settings.Library.Last(), typeof(Visibility), null!, System.Globalization.CultureInfo.InvariantCulture) is Visibility.Collapsed, "Uninstalled PKG must not expose a launch action");
                Check(store.Load().Downloads.Last().State == "Downloaded", "Persisted completion");
                Check(!File.ReadAllText(store.FilePath).Contains("temporary-secret"), "Signed transfer URLs must not be persisted");
                if (endpoint == "/unknown") Check(job.TotalBytes is null, "Unknown Content-Length");
                window.Close(); window = null;
                Console.WriteLine("PASS WebView2 landing + " + endpoint + " native download");
            }

            var acceptedCount = settings.Downloads.Count;
            var rejectedPrompts = 0;
            var unexpected = new DownloadMirror { Artifact = artifact, Url = server.Url("/landing?target=/unrelated"), Provider = "Local fixture", ExpectedFileName = "fixture.pkg" };
            window = new BrowserDownloadWindow(entry, unexpected, manager, profile, message => { rejectedPrompts++; return false; });
            window.Show(); await window.Ready.Task;
            await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await WaitAsync(() => Task.FromResult(rejectedPrompts == 1));
            Check(settings.Downloads.Count == acceptedCount, "Unrelated download must not create a job");
            var blocked = new TaskCompletionSource<bool>();
            window.Browser.CoreWebView2.NavigationStarting += (_, e) => { if (e.Uri.StartsWith("file:")) blocked.TrySetResult(e.Cancel); };
            window.Browser.CoreWebView2.Navigate("file:///C:/Windows/win.ini");
            Check(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Local file navigation must be blocked");
            await window.Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllProfile);
            window.Close(); window = null;
            Console.WriteLine("PASS unrelated download rejected, local-file navigation blocked, profile data cleared");

            var partial = new DownloadMirror { Artifact = new CatalogArtifact { Id = "partial" }, Url = server.Url("/landing?target=/partial"), Provider = "Local fixture" };
            window = new BrowserDownloadWindow(entry, partial, manager, profile, _ => true);
            window.Show(); await window.Ready.Task;
            await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await WaitAsync(() => Task.FromResult(settings.Downloads.Count == acceptedCount + 1 && settings.Downloads.Last().State == "Interrupted"));
            Check(!File.Exists(settings.Downloads.Last().Destination) && settings.Library.Count == 3, "Partial transfer must not publish");
            window.Close(); window = null;
            Console.WriteLine("PASS partial browser transfer remains interrupted");

            var slow = new DownloadMirror { Artifact = new CatalogArtifact { Id = "slow" }, Url = server.Url("/landing?target=/slow"), Provider = "Local fixture" };
            window = new BrowserDownloadWindow(entry, slow, manager, profile, _ => true);
            window.Show(); await window.Ready.Task;
            await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await WaitAsync(() => Task.FromResult(settings.Downloads.Count == acceptedCount + 2 && settings.Downloads.Last().DownloadedBytes > 0));
            var active = settings.Downloads.Last(); manager.Pause(active);
            await WaitAsync(() => Task.FromResult(active.State == "Paused"));
            Check(store.Load().Downloads.Last().State == "Interrupted", "Restart cannot treat paused transfer as complete");
            if (active.CanResume)
            {
                manager.Resume(active); await WaitAsync(() => Task.FromResult(active.State == "Downloaded"));
                Check(new FileInfo(active.Destination).Length == 4 * 1024 * 1024, "Resumed file bytes");
                Console.WriteLine("PASS native pause/resume");
            }
            else { manager.Cancel(active); Console.WriteLine("Resume is unavailable for this native transfer"); }
            window.Close(); window = null;

            foreach (var closeInstead in new[] { false, true })
            {
                var before = settings.Downloads.Count;
                window = new BrowserDownloadWindow(entry, slow, manager, profile, _ => true);
                window.Show(); await window.Ready.Task;
                await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
                await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
                await WaitAsync(() => Task.FromResult(settings.Downloads.Count == before + 1 && settings.Downloads.Last().DownloadedBytes > 0));
                var incomplete = settings.Downloads.Last();
                if (closeInstead) window.Close(); else { await manager.CancelAsync(incomplete); window.Close(); }
                window = null;
                Check(!File.Exists(incomplete.Destination), "Cancelled or closed browser must not publish");
                Check(store.Load().Downloads.Last().State == (closeInstead ? "Interrupted" : "Cancelled"), "Persisted terminal state: close=" + closeInstead + ", actual=" + store.Load().Downloads.Last().State + ", memory=" + incomplete.State);
                Check(incomplete.Error.Contains(closeInstead ? "interrupted by window closure" : "WebView2 transfer cancelled") &&
                    incomplete.Error.Contains(closeInstead ? "partial bytes may be unavailable" : "may remove partial bytes"), "Native terminal guidance does not promise partial retention");
            }
            Console.WriteLine("PASS cancellation + close/restart during native transfer");
            var loaded = store.Load(); Check(loaded.Library.Count == settings.Downloads.Count(x => x.State == "Downloaded"), "Only completed verified files belong in library");
            var importPath = Path.Combine(root, "external.pkg"); File.WriteAllBytes(importPath, server.Bytes);
            await manager.ImportAsync(entry, unexpected, importPath);
            Check(store.Load().Downloads.Last().State == "Imported" && File.Exists(settings.Downloads.Last().Destination), "Explicit external file import");
            Console.WriteLine("PASS actual files, persisted progress and library records");

            var modelStore = new SettingsStore(Path.Combine(root, "viewmodel-settings"));
            var completed = Path.Combine(root, "completed.pkg"); File.WriteAllBytes(completed, server.Bytes);
            modelStore.Save(new AppSettings { DownloadRoot = Path.Combine(root, "viewmodel-downloads"),
                Downloads = [new() { Id = "complete", State = "Downloaded", Destination = completed, DownloadedBytes = server.Bytes.Length, ExpectedSha256 = artifact.Sha256 },
                    new() { Id = "failed", State = "Failed", Destination = Path.Combine(root, "failed.pkg") }],
                Library = [new() { DownloadPath = completed, ContentPath = completed, Integrity = "SHA-256 verified at download" }] });
            using (var model = new MainViewModel(modelStore))
            {
                model.SelectedJob = model.Downloads.Single(x => x.Id == "failed"); model.CancelSelectedDownload();
                Check(model.Downloads.Count == 2, "Inactive cancellation keeps incomplete history");
                await model.DiscardSelectedDownloadAsync();
                Check(model.Downloads.Count == 1, "Explicit discard clears failed direct job with no remaining temporary data");
                var running = model.DownloadAsync(entry, new CatalogArtifact { Id = "slow-direct", Name = "slow.pkg", Url = server.Url("/slow") });
                await WaitAsync(() => Task.FromResult(model.Downloads.Any(x => x.State == "Downloading" && x.DownloadedBytes > 0)));
                var queued = model.DownloadAsync(entry, new CatalogArtifact { Id = "queued-direct", Name = "queued.pkg", Url = server.Url("/file") });
                var queuedJob = model.Downloads.Last(); model.SelectedJob = queuedJob; model.CancelSelectedDownload(); await queued;
                Check(queuedJob.State == "Cancelled" && !File.Exists(queuedJob.Destination), "Queued cancellation prevents a later transfer");
                Check(queuedJob.Error.Contains("cancelled before its transfer started") && File.ReadAllText(modelStore.FilePath).Contains(queuedJob.Error),
                    "Queued cancellation guidance persists without claiming an active transfer stopped");
                var queuedAgain = model.DownloadAsync(entry, new CatalogArtifact { Id = "queued-again", Name = "queued-again.pkg", Url = server.Url("/file") });
                window = new BrowserDownloadWindow(entry, slow, model.BrowserDownloads, profile, _ => true);
                window.Show(); await window.Ready.Task;
                await WaitAsync(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
                await window.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
                await WaitAsync(() => Task.FromResult(model.Downloads.Any(x => x.Method == "Browser" && x.DownloadedBytes > 0)));
                await model.ClearAllDownloadsAsync();
                Check(model.Downloads.Count == 4 && !running.IsCompleted && !queuedAgain.IsCompleted && model.BrowserDownloads.HasActive,
                    "Clear completed history preserves active writers and incomplete records");
                Check(model.Library.Count == 1 && File.ReadAllBytes(completed).SequenceEqual(server.Bytes), "History clearing preserves completed bytes and library");
                var native = model.Downloads.Single(x => x.Method == "Browser");
                model.SelectedJob = native;
                var discard = model.DiscardSelectedDownloadAsync();
                if (model.BrowserDownloads.IsActive(native)) Check(model.Downloads.Contains(native), "Native writer remains visible until definitive release");
                await discard;
                Check(!model.BrowserDownloads.IsActive(native) && !model.Downloads.Contains(native) && !File.Exists(native.StagingPath) && !File.Exists(native.StagingPath + ".crdownload"), "Discard waits for native termination and removes partial");
                foreach (var job in model.Downloads.ToArray()) { model.SelectedJob = job; model.CancelSelectedDownload(); }
                await Task.WhenAll(running, queuedAgain);
                Check(model.Settings.Downloads.Single(x => x.Id == "complete").ExpectedSha256 == artifact.Sha256, "Library verification metadata remains available");
                window.Close(); window = null;
            }
            using (var reopened = new MainViewModel(modelStore))
            {
                Check(reopened.Downloads.Count == 3 && reopened.Library.Count == 1, "Kept incomplete rows survive restart and cleared completed history stays cleared");
                await reopened.ReinspectAsync(reopened.Library.Single());
                Check(reopened.Library.Single().Integrity == "SHA-256 verified on reinspection", "Clearing the list preserves usable checksum verification");
            }
            Console.WriteLine("PASS explicit keep/discard, queued cancel, native writer wait, history-only Clear all and persisted library preservation");
        }
        finally
        {
            manager.Dispose(); window?.Close();
            // WebView2 subprocesses release the app-owned test profile asynchronously.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) { await Task.Delay(200); }
            }
            if (Directory.Exists(root)) Console.WriteLine("Test profile remains in use: " + root);
        }
    }
    private static async Task VerifyArtworkDetachAsync()
    {
        var tile = new CoverThumbnail { Width = 160, Height = 160 };
        var panel = new System.Windows.Controls.StackPanel(); panel.Children.Add(tile);
        var window = new Window { Content = new System.Windows.Controls.ScrollViewer { Content = panel }, Width = 220, Height = 220 };
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Check(tile.IsLoaded, "Artwork fixture did not load.");
            panel.Children.Remove(tile);
            // Reproduce a filter change before the deferred Unloaded event runs.
            tile.Url = "https://example.invalid/cover.png";
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            panel.Children.Add(tile); tile.Url = "";
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Console.WriteLine("PASS artwork detach/reload during catalogue filtering");
        }
        finally { window.Close(); }
    }
    private static async Task WaitAsync(Func<Task<bool>> check)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!await check()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Browser fixture did not reach expected state."); await Task.Delay(50); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

internal sealed class BrowserFixtures : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly int port;
    public byte[] Artwork { get; set; } = [];
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> Requests { get; } = new();
    public byte[] Bytes { get; } = Enumerable.Range(0, 131072).Select(x => (byte)(x % 251)).ToArray();
    public BrowserFixtures()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        listener.Prefixes.Add(Url("/")); listener.Start(); _ = Task.Run(Loop);
    }
    public string Url(string path) => $"http://127.0.0.1:{port}{path}";
    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); } catch { return; }
            _ = Serve(context);
        }
    }
    private async Task Serve(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            Requests.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (path == "/cover")
            {
                context.Response.ContentType = "image/png"; context.Response.ContentLength64 = Artwork.Length;
                await context.Response.OutputStream.WriteAsync(Artwork);
            }
            else if (path == "/bad-cover")
            {
                var html = Encoding.UTF8.GetBytes("<html>Not an image</html>"); context.Response.ContentType = "text/html";
                await context.Response.OutputStream.WriteAsync(html);
            }
            else if (path == "/verification")
            {
                var mode = context.Request.QueryString["mode"] ?? "passive";
                var target = context.Request.QueryString["target"] ?? "/redirect";
                // Inputs belong only to this loopback test server; HTML encoding prevents markup injection.
                var action = mode == "no-signal" ? "document.body.dataset.verified='true'" : mode == "popup" ?
                    "window.open('" + target + "','_blank')" : "setTimeout(()=>location.href='" + target + "',100)";
                var html = Encoding.UTF8.GetBytes(mode == "automatic-popup" ? "<html><body><script>setTimeout(()=>window.open('/redirect','_blank'),200)</script></body></html>" :
                    "<html><body><button id='verify' onclick=\"" + WebUtility.HtmlEncode(action) + "\">Simulate verification</button></body></html>");
                context.Response.ContentType = "text/html"; context.Response.ContentLength64 = html.Length;
                await context.Response.OutputStream.WriteAsync(html);
            }
            else if (path == "/popup-file")
            {
                var html = Encoding.UTF8.GetBytes("<html><body><button id='download' onclick=\"window.open('/popup-ad','_blank');setTimeout(()=>location.href='/file',250)\">Download synthetic file</button></body></html>");
                context.Response.ContentType = "text/html"; context.Response.ContentLength64 = html.Length; await context.Response.OutputStream.WriteAsync(html);
            }
            else if (path == "/landing")
            {
                var target = context.Request.QueryString["target"] ?? "/file";
                var html = Encoding.UTF8.GetBytes("<html><body><h1>Local download fixture</h1><a id='download' href='" + target + "'>Download fixture</a></body></html>");
                context.Response.ContentType = "text/html"; context.Response.ContentLength64 = html.Length; await context.Response.OutputStream.WriteAsync(html);
            }
            else if (path == "/redirect") { context.Response.StatusCode = 302; context.Response.RedirectLocation = Url("/file?token=temporary-secret"); }
            else if (path is "/file" or "/unknown" or "/partial" or "/unrelated" or "/slow")
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + (path == "/unrelated" ? "unrelated.exe" : "fixture.pkg") + "\"";
                var data = path == "/slow" ? new byte[4 * 1024 * 1024] : Bytes;
                var offset = 0;
                if (path == "/slow")
                {
                    context.Response.Headers["Accept-Ranges"] = "bytes";
                    if (context.Request.Headers["Range"] is string range && int.TryParse(range.Replace("bytes=", "").TrimEnd('-'), out offset))
                    {
                        context.Response.StatusCode = 206; context.Response.Headers["Content-Range"] = $"bytes {offset}-{data.Length - 1}/{data.Length}";
                    }
                }
                if (path == "/unknown") context.Response.SendChunked = true;
                else context.Response.ContentLength64 = path == "/partial" ? data.Length * 2 : data.Length - offset;
                if (path == "/slow")
                    for (var i = offset; i < data.Length; i += 8192) { await context.Response.OutputStream.WriteAsync(data.AsMemory(i, Math.Min(8192, data.Length - i))); await Task.Delay(10); }
                else await context.Response.OutputStream.WriteAsync(data);
                if (path == "/partial") { context.Response.Abort(); return; }
            }
            else context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch { context.Response.Abort(); }
    }
    public void Dispose() { listener.Stop(); listener.Close(); }
}
