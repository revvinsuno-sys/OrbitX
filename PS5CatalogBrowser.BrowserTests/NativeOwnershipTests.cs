using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class NativeOwnershipTests
{
    public static async Task RunAsync()
    {
        await CallbackFaultsAsync();
        await CompletionAsync();
        await CancellationAsync();
        await WindowClosureAsync();
        await CleanupAsync();
        await RecoveryAsync();
        Console.WriteLine("PASS native ownership: callbacks, persistence, admission, cancellation, closure, A3/A4 races, files, recovery and secret exclusion");
    }

    private static async Task CallbackFaultsAsync()
    {
        foreach (var fault in new[] { "progress", "state", "progress-save", "notification", "cancel" })
        {
            using var fixture = new Fixture();
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 8);
            var attempts = 0;
            if (fault is "progress" or "cancel") fixture.Operation.ThrowOnBytes = true;
            if (fault == "state") fixture.Operation.ThrowOnState = true;
            if (fault == "progress-save") fixture.Store.BeforeSave = _ => { attempts++; throw new IOException(fixture.Operation.Uri); };
            if (fault == "notification") fixture.OnChanged = (_, _) => { attempts++; throw new InvalidOperationException(fixture.Operation.Uri); };
            if (fault == "cancel") fixture.Operation.ThrowOnCancel = true;
            await Task.Delay(270); // Exercise the existing notification throttle.
            if (fault == "state") fixture.Operation.NotifyState(); else fixture.Operation.NotifyProgress();
            Check(fixture.Manager.IsActive(job) && fixture.Admission.Owns(job) && fixture.Operation.Subscribed,
                fault + ": an active writer retains manager, admission and native event ownership");
            Check(fixture.Operation.CancelRequests == 1 && !job.CanPause && !job.CanResume && job.Error.Length > 0,
                fault + ": fault requests cancellation and exposes a recoverable error");
            Reject(() => fixture.Accept(), "Duplicate admission stays exclusive during callback failure");
            fixture.Operation.ThrowOnBytes = fixture.Operation.ThrowOnState = false;
            var previousAttempts = attempts;
            for (var repeat = 0; repeat < 5; repeat++) fixture.Operation.Write(job.StagingPath, 16 + repeat);
            Check(attempts == previousAttempts && fixture.Operation.CancelRequests == 1, "Faulted progress must not retry failing notifications/persistence indefinitely");
            Check(File.Exists(job.StagingPath) && !File.Exists(job.Destination) && fixture.Settings.Library.Count == 0,
                "Delayed/failed cancellation preserves the active writer's staging and cannot publish partial content");
            fixture.Store.BeforeSave = null; fixture.OnChanged = null; fixture.Operation.ThrowOnCancel = false;
            var cancellation = fixture.Manager.CancelAsync(job);
            Check(!cancellation.IsCompleted && fixture.Manager.IsActive(job), "Explicit cancellation remains available while the writer is unproven");
            fixture.Operation.Terminate(); await cancellation;
            Check(!fixture.Manager.IsActive(job) && !fixture.Admission.Owns(job) && !fixture.Operation.Subscribed && File.Exists(job.StagingPath),
                "Termination releases ownership/events and keeps incomplete bytes");
            Check(fixture.Store.Load().Downloads.Single().State == "Cancelled", "Explicit terminal cancellation persists");
            NoSecrets(fixture.Store, job);
        }
        using (var fixture = new Fixture())
        {
            var before = File.ReadAllBytes(fixture.Store.FilePath);
            fixture.Store.BeforeSave = _ => throw new IOException("initial save failure");
            Reject(() => fixture.Accept(), "Initial admission save failure rejects before native ownership");
            Check(!fixture.Manager.HasActive && fixture.Settings.Downloads.Count == 0 && fixture.Operation.CancelRequests == 0 &&
                File.ReadAllBytes(fixture.Store.FilePath).SequenceEqual(before) &&
                !Directory.EnumerateFiles(fixture.Settings.DownloadRoot, ".owner", SearchOption.AllDirectories).Any(),
                "Initial admission rollback leaves no native writer, persisted job or staging marker");
            fixture.Store.BeforeSave = null;
            var job = fixture.Accept(); fixture.Operation.Terminate();
            Check(!fixture.Manager.IsActive(job), "Rejected admission does not reserve the selection");
        }
        using (var fixture = new Fixture())
        {
            var associated = new List<DownloadJob>();
            fixture.OnChanged = (_, _) => throw new IOException(fixture.Operation.Uri);
            Reject(() => fixture.Manager.Accept(fixture.Entry, fixture.Mirror, "fixture.pkg", fixture.Operation, owned: associated.Add),
                "First post-admission notification failure is rejected");
            var job = associated.Single();
            Check(fixture.Manager.IsActive(job) && fixture.Admission.Owns(job) && fixture.Operation.Subscribed,
                "The window receives ownership before initial callback failure");
            fixture.OnChanged = null; fixture.Operation.Terminate();
            Check(!fixture.Manager.IsActive(job) && fixture.Store.Load().Downloads.Single().State == "Interrupted", "Failed acceptance stops without losing its job");
            NoSecrets(fixture.Store, job);
        }
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 16);
            fixture.Store.BeforeSave = _ => throw new IOException(fixture.Operation.Uri);
            fixture.Operation.Terminate();
            Check(!fixture.Manager.IsActive(job) && !fixture.Admission.Owns(job) && job.Error.Contains("could not be saved") &&
                fixture.Store.Load().Downloads.Single().State == "Interrupted" && File.Exists(job.StagingPath) && fixture.Store.Load().Library.Count == 0,
                "Terminal persistence failure releases only the stopped writer, reports failure and preserves the last durable record");
            NoSecrets(fixture.Store, job); fixture.Store.BeforeSave = null;
        }
    }

    private static async Task CompletionAsync()
    {
        foreach (var scenario in new[] { "normal", "duplicate", "cancel", "notification" })
        {
            using var fixture = new Fixture();
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 64);
            var verifications = 0; var registrations = 0; var reentered = false;
            fixture.Store.BeforeSave = candidate =>
            {
                if (candidate.Downloads.Single().State == "Verifying download" && candidate.Downloads.Single().Completion is null) verifications++;
            };
            fixture.OnChanged = (record, item) =>
            {
                if (item is not null) { registrations++; if (scenario == "notification") throw new IOException(fixture.Operation.Uri); }
                if (reentered || fixture.Operation.State != CoreWebView2DownloadState.Completed) return;
                reentered = true;
                Check(fixture.Manager.IsActive(record) && fixture.Manager.IsVerifying && fixture.Admission.Owns(record), "A4 claims ownership before the first terminal notification");
                if (scenario == "duplicate") { fixture.Operation.Complete(); fixture.Operation.NotifyState(); }
                if (scenario == "cancel")
                {
                    Reject(() => fixture.Manager.Cancel(record), "Cancellation cannot detach verification owner");
                    fixture.Manager.Interrupt(record);
                    Check(fixture.Manager.IsActive(record), "Browser closure during verification retains A4 owner");
                }
            };
            fixture.Operation.Complete();
            for (var repeat = 0; repeat < 3; repeat++) fixture.Operation.NotifyState();
            await WaitAsync(() => !fixture.Manager.IsActive(job));
            Check(job.State == "Downloaded" && job.Completion?.Phase == "Completed" && verifications == 1 && registrations == 1 &&
                fixture.Settings.Library.Count == 1 && fixture.Store.Load().Library.Count == 1 &&
                File.ReadAllBytes(job.Destination).SequenceEqual(Enumerable.Repeat((byte)42, 64)) && !File.Exists(job.StagingPath),
                scenario + ": one verification, promotion, durable registration and correct final bytes");
            Check(!fixture.Admission.Owns(job) && !fixture.Operation.Subscribed, "Durable completion releases native events and admission");
            if (scenario == "notification") Check(job.Error.Contains("notification failed"), "Durable completion reports UI notification failure honestly");
            NoSecrets(fixture.Store, job);
        }
        using (var fixture = new Fixture())
        {
            fixture.Mirror.Artifact.Size = 64;
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 8); fixture.Operation.Complete();
            await WaitAsync(() => !fixture.Manager.IsActive(job));
            Check(job.Completion is null && job.State == "Failed" && File.Exists(job.StagingPath) && !File.Exists(job.Destination) && fixture.Store.Load().Library.Count == 0,
                "A partial native Completed notification cannot publish or register content");
        }
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 64);
            fixture.Store.BeforeSave = candidate => { if (candidate.Downloads.Single().State == "Verifying download") throw new IOException(fixture.Operation.Uri); };
            fixture.Operation.Complete(); await WaitAsync(() => !fixture.Manager.IsActive(job));
            Check(job.State == "Failed" && job.Completion is null && File.Exists(job.StagingPath) && !File.Exists(job.Destination) && fixture.Store.Load().Library.Count == 0,
                "A4 pre-intent persistence failure keeps stopped staging and cannot claim durable success");
            NoSecrets(fixture.Store, job); fixture.Store.BeforeSave = null;
        }
    }

    private static async Task CancellationAsync()
    {
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept();
            fixture.Operation.Write(job.StagingPath, 8);
            fixture.Manager.Dispose();
            Check(fixture.Manager.IsActive(job) && fixture.Admission.Owns(job), "Shutdown must retain delayed cancellation ownership");
            fixture.Operation.Terminate();
            Check(!fixture.Manager.IsActive(job) && !fixture.Admission.Owns(job) && !fixture.Operation.Subscribed &&
                fixture.Store.Load().Downloads.Single().State == "Interrupted" && File.Exists(job.StagingPath), "Shutdown observes delayed native termination and preserves bytes");
        }
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept();
            fixture.Operation.Write(job.StagingPath, 64);
            fixture.Operation.CompleteOnCancel = true;
            await fixture.Manager.CancelAsync(job);
            Check(job.State == "Downloaded" && fixture.Store.Load().Library.Count == 1 && File.Exists(job.Destination), "Completion winning native cancellation proceeds through A4 once");
        }
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 64);
            fixture.Manager.Pause(job); fixture.Manager.Resume(job);
            fixture.Operation.Write(job.StagingPath, 8);
            Check(job.DownloadedBytes == 8 && job.BytesPerSecond >= 0 && fixture.Manager.IsActive(job), "Native retry byte reset preserves ownership and nonnegative speed");
            fixture.Manager.Cancel(job); fixture.Manager.Cancel(job);
            Check(fixture.Manager.IsActive(job) && fixture.Admission.Owns(job), "Repeated cancellation cannot release an active native writer");
            fixture.Operation.Terminate(); fixture.Manager.Cancel(job);
            Check(!fixture.Manager.IsActive(job) && fixture.Store.Load().Downloads.Single().State == "Cancelled" && new FileInfo(job.StagingPath).Length == 8,
                "Repeated cancellation after termination is harmless and retains the partial");
        }
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 16);
            var reentered = false;
            fixture.OnChanged = (_, _) =>
            {
                if (reentered) return;
                reentered = true; fixture.Operation.Terminate();
            };
            fixture.Operation.NotifyState();
            Check(!fixture.Manager.IsActive(job) && !fixture.Admission.Owns(job) && fixture.Store.Load().Downloads.Single().State == "Cancelled",
                "A terminal event inside a nonterminal callback is processed after callback re-entry");
        }
        using (var fixture = new Fixture())
        {
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 16);
            var reentered = false;
            fixture.OnChanged = (record, _) =>
            {
                if (reentered || !record.Error.Contains("persistence failed")) return;
                reentered = true; fixture.Manager.Cancel(record);
            };
            fixture.Operation.ThrowOnBytes = true; fixture.Operation.NotifyProgress(); fixture.Operation.ThrowOnBytes = false;
            Check(reentered && fixture.Manager.IsActive(job) && fixture.Admission.Owns(job) && fixture.Operation.CancelRequests == 2,
                "User cancellation during fault notification preserves the same native owner");
            fixture.Operation.Terminate();
            Check(fixture.Store.Load().Downloads.Single().State == "Cancelled" && File.Exists(job.StagingPath), "Fault-handling cancellation persists only after terminal confirmation");
        }
    }

    private static async Task WindowClosureAsync()
    {
        using var server = new BrowserFixtures();
        foreach (var scenario in new[] { "active", "verification", "acceptance-failure" })
        {
            using var fixture = new Fixture();
            var windowMirror = new DownloadMirror { Url = server.Url("/landing?target=/file"), Artifact = fixture.Mirror.Artifact };
            var window = new BrowserDownloadWindow(fixture.Entry, windowMirror, fixture.Manager, Path.Combine(fixture.Root, "profile"), _ => true);
            window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            DownloadJob job;
            if (scenario == "acceptance-failure")
            {
                fixture.OnChanged = (_, _) => throw new IOException(fixture.Operation.Uri);
                Reject(() => window.AcceptNativeDownload("fixture.pkg", fixture.Operation), "Failed first callback still associates the window");
                job = fixture.Settings.Downloads.Single(); fixture.OnChanged = null;
            }
            else job = window.AcceptNativeDownload("fixture.pkg", fixture.Operation);
            fixture.Operation.Write(job.StagingPath, 64);
            if (scenario == "verification")
            {
                var closureProtected = false;
                fixture.OnChanged = (record, _) =>
                {
                    if (record.State != "Checking integrity") return;
                    window.Close(); closureProtected = window.IsVisible && fixture.Manager.IsActive(record);
                };
                fixture.Operation.Complete(); await WaitAsync(() => !fixture.Manager.IsActive(job));
                Check(closureProtected && job.State == "Downloaded" && fixture.Store.Load().Library.Count == 1,
                    "Window close during verification retains the browser and allows durable A4 completion");
            }
            else
            {
                window.Close();
                Check(window.IsVisible && fixture.Manager.IsActive(job) && fixture.Operation.Subscribed, "Window remains open with a controllable native writer until cancellation is confirmed");
                fixture.Operation.Write(job.StagingPath, 72); fixture.Operation.Terminate();
                Check(fixture.Store.Load().Downloads.Single().State == "Interrupted" && File.Exists(job.StagingPath), "Closing preserves staging and persists interrupted termination");
            }
            window.Close(); Check(!window.IsVisible, "Confirmed termination permits closing the browser");
        }
        var appRoot = Path.Combine(Path.GetTempPath(), "PS5NativeAppClose-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(appRoot, "settings"));
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(appRoot, "downloads") });
        var mainWindow = new MainWindow(store);
        var model = (MainViewModel)mainWindow.DataContext;
        var operation = new ControlledOperation();
        try
        {
            mainWindow.Show();
            var job = model.BrowserDownloads.Accept(new() { Id = "close", Title = "Application closure" },
                new() { Url = "https://example.test/landing", Artifact = new() { Id = "close", Url = "https://example.test/file" } }, "close.pkg", operation);
            operation.Write(job.StagingPath, 16);
            model.BrowserDownloads.Dispose(); // An already-confirmed shutdown, avoiding the user confirmation dialog.
            mainWindow.Close();
            Check(mainWindow.IsVisible && model.BrowserDownloads.IsActive(job), "Application close waits for native writer termination");
            Reject(() => store.AcquireApplicationOwnership(), "Pending shutdown keeps application settings ownership");
            operation.Terminate();
            Check(!model.BrowserDownloads.IsActive(job) && store.Load().Downloads.Single().State == "Interrupted", "Application shutdown continues persisting terminal callbacks");
            mainWindow.Close();
            Check(!mainWindow.IsVisible && File.Exists(job.StagingPath), "Confirmed application shutdown retains partial data");
            using var released = store.AcquireApplicationOwnership();
        }
        finally { operation.Terminate(); mainWindow.Close(); Directory.Delete(appRoot, true); }
    }

    private static async Task CleanupAsync()
    {
        foreach (var completing in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "PS5NativeCleanup-" + Guid.NewGuid().ToString("N"));
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
            var operation = new ControlledOperation();
            try
            {
                using var model = new MainViewModel(store);
                var entry = new CatalogEntry { Id = "local", Title = "Cleanup ownership" };
                var mirror = new DownloadMirror { Url = "https://example.test/landing", Artifact = new() { Id = "file", Url = "https://example.test/file" } };
                var job = model.BrowserDownloads.Accept(entry, mirror, "fixture.pkg", operation);
                operation.Write(job.StagingPath, 64); model.SelectedJob = job;
                // MainViewModel's notification adapter must propagate, rather than swallow, a save failure.
                store.BeforeSave = _ => throw new IOException(operation.Uri);
                await Task.Delay(270); operation.NotifyProgress();
                Check(operation.CancelRequests == 1 && model.BrowserDownloads.IsActive(job) && job.Error.Contains("persistence failed"), "WPF persistence failure reaches native fault handling");
                store.BeforeSave = null;
                await model.ClearAllDownloadsAsync();
                Check(model.Downloads.Contains(job) && File.Exists(job.StagingPath), "Clear History preserves faulted active staging");
                var discarding = model.DiscardSelectedDownloadAsync();
                Check(!discarding.IsCompleted && File.Exists(job.StagingPath) && model.BrowserDownloads.IsActive(job), "A3 waits for native termination before any staging deletion");
                operation.Write(job.StagingPath, 72);
                if (completing) operation.Complete(); else operation.Terminate();
                await discarding;
                // A transfer fault deliberately retains even a later Completed transfer as incomplete.
                Check(!model.BrowserDownloads.IsActive(job) && !File.Exists(job.StagingPath) && model.Library.Count == 0,
                    "Only definitive termination permits explicit incomplete cleanup");
                Check(store.Load().Downloads.All(record => record.IsCleared), "Explicit cleanup result persists");
            }
            finally { operation.Terminate(); Directory.Delete(root, true); }
        }
        var raceRoot = Path.Combine(Path.GetTempPath(), "PS5NativeClearRace-" + Guid.NewGuid().ToString("N"));
        var raceStore = new SettingsStore(Path.Combine(raceRoot, "settings"));
        raceStore.Save(new() { Sources = [], DownloadRoot = Path.Combine(raceRoot, "downloads") });
        var raceOperation = new ControlledOperation();
        try
        {
            using var model = new MainViewModel(raceStore);
            var job = model.BrowserDownloads.Accept(new() { Id = "race", Title = "Completion cleanup race" },
                new() { Url = "https://example.test/landing", Artifact = new() { Id = "race", Url = "https://example.test/file" } }, "race.pkg", raceOperation);
            model.SelectedJob = job; raceOperation.Write(job.StagingPath, 64); raceOperation.CompleteOnCancel = true;
            await model.DiscardSelectedDownloadAsync();
            Check(job.State == "Downloaded" && model.Library.Count == 1 && File.Exists(job.Destination) && model.Downloads.Contains(job), "Completion racing Discard preserves the new library file and history");
            Check(raceStore.Load().Library.Count == 1, "Completion/Clear race registers exactly once durably");
        }
        finally { raceOperation.Terminate(); Directory.Delete(raceRoot, true); }
    }

    private static async Task RecoveryAsync()
    {
        foreach (var phase in new[] { "Pending", "Promoted", "Completed" })
        {
            using var fixture = new Fixture();
            var job = fixture.Accept(); fixture.Operation.Write(job.StagingPath, 64);
            var fired = false;
            fixture.Store.BeforeSave = candidate =>
            {
                if (!fired && candidate.Downloads.Single().Completion?.Phase == phase)
                { fired = true; throw new IOException(fixture.Operation.Uri); }
            };
            fixture.Operation.Complete(); await WaitAsync(() => !fixture.Manager.IsActive(job));
            Check(fired && job.State == "Needs attention" && fixture.Admission.Owns(job) && fixture.Settings.Library.Count == 0 && fixture.Store.Load().Library.Count == 0,
                phase + ": A4 failure preserves recovery intent and A2 admission without durable success");
            Check(phase == "Pending" ? File.Exists(job.StagingPath) && !File.Exists(job.Destination) : File.Exists(job.Destination) && !File.Exists(job.StagingPath),
                phase + ": files match the last safe promotion boundary");
            NoSecrets(fixture.Store, job); fixture.Store.BeforeSave = null;
            for (var restart = 0; restart < 2; restart++)
            {
                var recovered = fixture.Store.LoadForStartup();
                Check(recovered.Downloads.Single().State == "Downloaded" && recovered.Library.Count == 1 &&
                    recovered.Library.Single().DownloadArtifacts.Count == 1 && File.ReadAllBytes(job.Destination).SequenceEqual(Enumerable.Repeat((byte)42, 64)),
                    "Repeated recovery preserves A4 bytes and creates exactly one registration");
            }
        }
    }

    private static void NoSecrets(SettingsStore store, DownloadJob job) => Check(!job.Error.Contains("temporary-secret") &&
        !File.ReadAllText(store.FilePath).Contains("temporary-secret") && !File.ReadAllText(store.FilePath).Contains("session="), "Native capability URLs cannot enter manifest or diagnostic errors");
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task WaitAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Controlled lifecycle did not terminate"); await Task.Delay(10); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PS5NativeOwner-" + Guid.NewGuid().ToString("N"));
        public SettingsStore Store { get; }
        public AppSettings Settings { get; } = new() { Sources = [] };
        public DownloadAdmission Admission { get; } = new();
        public ControlledOperation Operation { get; } = new();
        public BrowserDownloadManager Manager { get; }
        public Action<DownloadJob, LocalLibraryEntry?>? OnChanged { get; set; }
        public CatalogEntry Entry { get; } = new() { Id = "fixture", SourceId = "local", Title = "Native ownership" };
        public DownloadMirror Mirror { get; } = new() { Url = "https://example.test/landing", Artifact = new() { Id = "selected", Url = "https://example.test/catalogue" } };
        public Fixture()
        {
            Settings.DownloadRoot = Path.Combine(Root, "downloads");
            Store = new(Path.Combine(Root, "settings")); Store.Save(Settings);
            Manager = new(() => Settings.DownloadRoot, () => Settings.Downloads,
                job => { Settings.Downloads.Add(job); try { Store.Save(Settings); } catch { Settings.Downloads.Remove(job); throw; } },
                (job, item) => { OnChanged?.Invoke(job, item); Store.Save(Settings); }, Settings, Store.Save, Admission);
        }
        public DownloadJob Accept() => Manager.Accept(Entry, Mirror, "fixture.pkg", Operation);
        public void Dispose()
        {
            OnChanged = null;
            Store.BeforeSave = null;
            Operation.ThrowOnBytes = Operation.ThrowOnState = Operation.ThrowOnCancel = false;
            if (Operation.State == CoreWebView2DownloadState.InProgress) Operation.Terminate();
            Manager.Dispose();
            // WebView2 may briefly keep its test profile after the window has closed.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Directory.Delete(Root, true); return; }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
            throw new IOException("Native ownership fixture remains in use: " + Root);
        }
    }

    internal sealed class ControlledOperation : INativeDownloadOperation
    {
        public string Uri => "https://example.test/file?session=temporary-secret";
        public string MimeType => "application/octet-stream";
        public ulong? TotalBytesToReceive => null;
        private long bytes;
        private CoreWebView2DownloadState state = CoreWebView2DownloadState.InProgress;
        private FileStream? writer;
        public bool ThrowOnBytes { get; set; }
        public bool ThrowOnState { get; set; }
        public bool ThrowOnCancel { get; set; }
        public bool CompleteOnCancel { get; set; }
        public long BytesReceived => ThrowOnBytes ? throw new COMException(Uri) : bytes;
        public CoreWebView2DownloadState State => ThrowOnState ? throw new COMException(Uri) : state;
        public CoreWebView2DownloadInterruptReason InterruptReason { get; private set; }
        public bool CanResume => State == CoreWebView2DownloadState.Interrupted && InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused;
        public event EventHandler<object>? BytesReceivedChanged;
        public event EventHandler<object>? EstimatedEndTimeChanged;
        public event EventHandler<object>? StateChanged;
        public bool Subscribed => BytesReceivedChanged is not null && EstimatedEndTimeChanged is not null && StateChanged is not null;
        public int CancelRequests { get; private set; }
        public void Cancel() { CancelRequests++; if (ThrowOnCancel) throw new COMException(Uri); if (CompleteOnCancel) Complete(); }
        public void Pause() { state = CoreWebView2DownloadState.Interrupted; InterruptReason = CoreWebView2DownloadInterruptReason.UserPaused; NotifyState(); }
        public void Resume() { state = CoreWebView2DownloadState.InProgress; NotifyState(); }
        public void Terminate() { writer?.Dispose(); writer = null; state = CoreWebView2DownloadState.Interrupted; InterruptReason = CoreWebView2DownloadInterruptReason.UserCanceled; NotifyState(); }
        public void InterruptTransfer() { state = CoreWebView2DownloadState.Interrupted; InterruptReason = default; NotifyState(); }
        public void Complete() { writer?.Dispose(); writer = null; state = CoreWebView2DownloadState.Completed; NotifyState(); }
        public void NotifyState() => StateChanged?.Invoke(this, EventArgs.Empty);
        public void NotifyProgress() { BytesReceivedChanged?.Invoke(this, EventArgs.Empty); EstimatedEndTimeChanged?.Invoke(this, EventArgs.Empty); }
        public void Write(string path, int count)
        {
            Check(state == CoreWebView2DownloadState.InProgress, "Only a live native operation may write fixture bytes");
            writer ??= new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            writer.Position = 0; writer.SetLength(count); writer.Write(Enumerable.Repeat((byte)42, count).ToArray()); writer.Flush(true); bytes = count;
            NotifyProgress();
        }
    }
}
