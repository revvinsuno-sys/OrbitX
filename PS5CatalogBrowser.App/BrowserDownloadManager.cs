using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed class BrowserDownloadManager(Func<string> root, Func<IEnumerable<DownloadJob>> jobs,
    Action<DownloadJob> added, Action<DownloadJob, LocalLibraryEntry?> changed, AppSettings settings, Action<AppSettings> persist,
    DownloadAdmission? sharedAdmission = null) : IDisposable
{
    public sealed record ProviderSession(string Id, string Title, List<DownloadJob> Downloads, Action Restore, Action Minimize);
    private sealed record QueuedProvider(CatalogEntry Entry, DownloadMirror Mirror, DownloadJob Job);
    public System.Collections.ObjectModel.ObservableCollection<ProviderSession> Sessions { get; } = [];
    private readonly Queue<QueuedProvider> queuedProviders = new();
    public event Action<CatalogEntry, DownloadMirror, DownloadJob>? QueuedProviderReady;
    internal bool CanCheckStatus(string id) => Sessions.Any(session => session.Id == id || session.Downloads.Any(job => job.Id == id));
    internal void CheckStatus(string id) => Sessions.Single(session => session.Id == id || session.Downloads.Any(job => job.Id == id)).Restore();
    internal DownloadBandwidthCoordinator? Bandwidth { get; set; }
    private const string CancelledGuidance = "WebView2 transfer cancelled. Download record retained; WebView2 may remove partial bytes. Select Discard incomplete to remove any remaining owned data.";
    private const string WindowClosureGuidance = "WebView2 transfer interrupted by window closure. Download record retained; partial bytes may be unavailable. Open the mirror again to start a new transfer, or use the external browser and import the matching completed file.";
    private sealed class Transfer(DownloadJob job, INativeDownloadOperation operation)
    {
        public DownloadJob Job { get; } = job;
        public INativeDownloadOperation Operation { get; } = operation;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public long LastNotification { get; set; }
        public long InitialBytes { get; set; }
        public bool Finalizing { get; set; }
        public bool Faulted { get; set; }
        public bool HandlingState { get; set; }
        public bool StatePending { get; set; }
        public string? StopState { get; set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<object>? ProgressHandler { get; set; }
        public EventHandler<object>? StateHandler { get; set; }
        public Action<DownloadJob, string, string?>? Observe { get; set; }
        public void Report(string phase)
        {
            // Diagnostics/UI messages must never alter native ownership or persistence.
            try { Observe?.Invoke(Job, phase, null); } catch { }
        }
        public void ReportNative()
        {
            try { Observe?.Invoke(Job, "NativeStateChanged", Operation.State + "/" + Operation.InterruptReason); } catch { }
        }
    }
    private readonly DownloadAdmission admission = sharedAdmission ?? new();
    private readonly Dictionary<string, Transfer> transfers = [];
    private int imports;
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, TaskCompletionSource Finished)> importWriters = [];
    public bool HasActive => transfers.Count > 0 || imports > 0;
    public bool IsVerifying => imports > 0 || transfers.Values.Any(t => t.Finalizing);
    public bool IsActive(DownloadJob job) => transfers.ContainsKey(job.Id) || importWriters.ContainsKey(job.Id);
    public bool ShuttingDown { get; private set; }

    public bool QueueVikingFile(CatalogEntry entry, DownloadMirror mirror, string selectionIdentity)
    {
        var slotInUse = transfers.Count > 0 || Sessions.Any(session => session.Downloads.Any(job => job.State is "Queued" or "Downloading" or "Checking integrity" or "Downloaded" or "Imported"));
        if (!IsVikingFile(mirror.BrowserUrl) || !slotInUse) return false;
        if (queuedProviders.Any(q => q.Job.SelectionIdentity == selectionIdentity))
            throw new IOException("This game is already queued for the provider download slot.");
        var name = mirror.ExpectedFileName.Length > 0 ? mirror.ExpectedFileName : entry.Title + ".download";
        var job = admission.Admit([selectionIdentity],
            () =>
            {
                var prepared = BrowserDownloadFiles.Prepare(entry, mirror, root(), name, jobs());
                prepared.State = "Queued";
                prepared.Error = "Waiting for the current browser download to finish and its provider window to close.";
                prepared.Url = mirror.BrowserUrl;
                return [prepared];
            }, accepted => added(accepted[0]), rejected => BrowserDownloadFiles.DiscardStaging(rejected[0]))[0];
        queuedProviders.Enqueue(new(entry, mirror, job));
        foreach (var session in Sessions) session.Minimize();
        return true;
    }

    public static bool IsVikingFile(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("vikingfile.com", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Host.Equals("www.vikingfile.com", StringComparison.OrdinalIgnoreCase);

    public void ProviderWindowClosed(ProviderSession session)
    {
        Sessions.Remove(session);
        if (Sessions.Count != 0 || queuedProviders.Count == 0 || session.Downloads.Count == 0 ||
            session.Downloads.Any(job => IsActive(job) || job.State is not ("Downloaded" or "Imported"))) return;
        while (queuedProviders.TryDequeue(out var next))
        {
            if (next.Job.State != "Queued" || !admission.Owns(next.Job)) continue;
            QueuedProviderReady?.Invoke(next.Entry, next.Mirror, next.Job);
            break;
        }
    }

    public void DiscardQueuedProvider(DownloadJob job)
    {
        if (!queuedProviders.Any(item => ReferenceEquals(item.Job, job))) return;
        var remaining = queuedProviders.Where(item => !ReferenceEquals(item.Job, job)).ToArray();
        queuedProviders.Clear();
        foreach (var item in remaining) queuedProviders.Enqueue(item);
        BrowserDownloadFiles.DiscardStaging(job);
    }

    public DownloadJob Accept(CatalogEntry entry, DownloadMirror mirror, string suggestedName, CoreWebView2DownloadOperation operation, string? selectionIdentity = null)
        => Accept(entry, mirror, suggestedName, new NativeDownloadOperation(operation), selectionIdentity);

    internal DownloadJob Accept(CatalogEntry entry, DownloadMirror mirror, string suggestedName, INativeDownloadOperation operation, string? selectionIdentity = null,
        Action<DownloadJob>? owned = null, Action<DownloadJob, string, string?>? observe = null)
    {
        DownloadAdmission.EnsureEnabled(entry);
        OrbitDownloadPolicy.ValidateFileName(entry, mirror.Artifact, suggestedName);
        if (ShuttingDown) throw new InvalidOperationException("The browser download session is closing.");
        NetworkPolicy.Validate(operation.Uri);
        if (operation.MimeType.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HTML pages cannot be accepted as game content.");
        if (operation.TotalBytesToReceive is > long.MaxValue) throw new IOException("Browser download size is too large.");
        if (operation.TotalBytesToReceive is ulong total && total > 0 && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root()))!).AvailableFreeSpace < (long)total)
            throw new IOException("Insufficient disk space for browser download.");
        var identity = selectionIdentity ?? DownloadAdmission.Capture(entry, mirror.Artifact);
        var job = jobs().FirstOrDefault(candidate => candidate.State == "Queued" && candidate.SelectionIdentity == identity && admission.Owns(candidate));
        if (job is null)
            job = admission.Admit([identity],
                () => [BrowserDownloadFiles.Prepare(entry, mirror, root(), suggestedName, jobs())],
                accepted => added(accepted[0]), rejected => BrowserDownloadFiles.DiscardStaging(rejected[0]))[0];
        var queuedJob = job.State == "Queued";
        var transfer = new Transfer(job, operation) { Observe = observe };
        transfer.ProgressHandler = (_, _) =>
        {
            try { Update(transfer); }
            catch { CallbackFailed(transfer); }
        };
        transfer.StateHandler = async (_, _) =>
        {
            transfer.ReportNative();
            try { await StateChangedAsync(transfer); }
            catch { CallbackFailed(transfer); }
            finally { transfer.Report("ApplicationState"); }
        };
        transfers.Add(job.Id, transfer);
        try
        {
            // Associate the window before any native subscription/notification can fail.
            owned?.Invoke(job);
            if (queuedJob) { job.State = "Downloading"; job.Error = ""; changed(job, null); }
            operation.BytesReceivedChanged += transfer.ProgressHandler;
            operation.EstimatedEndTimeChanged += transfer.ProgressHandler;
            operation.StateChanged += transfer.StateHandler;
            Update(transfer, true);
        }
        catch
        {
            CallbackFailed(transfer);
            throw new IOException("Browser download could not be accepted. Its record was retained; partial bytes may be unavailable. Use the external browser and import the matching completed file.");
        }
        return job;
    }

    private void Update(Transfer transfer, bool force = false, bool completing = false)
    {
        if (!transfers.ContainsKey(transfer.Job.Id) || transfer.Finalizing && !completing || transfer.Faulted || transfer.StopState is not null || ShuttingDown) return;
        var op = transfer.Operation; var job = transfer.Job;
        job.DownloadedBytes = op.BytesReceived;
        job.TotalBytes = op.TotalBytesToReceive is ulong total && total is > 0 and <= long.MaxValue ? (long)total : null;
        if (op.BytesReceived < transfer.InitialBytes) { transfer.InitialBytes = op.BytesReceived; transfer.Clock.Restart(); }
        job.BytesPerSecond = (op.BytesReceived - transfer.InitialBytes) / Math.Max(0.001, transfer.Clock.Elapsed.TotalSeconds);
        job.CanPause = op.State == CoreWebView2DownloadState.InProgress;
        job.CanResume = op.State == CoreWebView2DownloadState.Interrupted && op.CanResume;
        if (force || transfer.Clock.ElapsedMilliseconds - transfer.LastNotification >= 250)
        {
            transfer.LastNotification = transfer.Clock.ElapsedMilliseconds;
            changed(job, null);
        }
    }

    private async Task StateChangedAsync(Transfer transfer)
    {
        if (transfer.Finalizing || !transfers.ContainsKey(transfer.Job.Id)) return;
        if (transfer.HandlingState) { transfer.StatePending = true; return; }
        transfer.HandlingState = true;
        try
        {
            do
            {
                transfer.StatePending = false;
                await ApplyStateAsync(transfer);
            } while (transfer.StatePending && !transfer.Finalizing && transfers.ContainsKey(transfer.Job.Id));
        }
        finally { transfer.HandlingState = false; }
    }

    private async Task ApplyStateAsync(Transfer transfer)
    {
        var op = transfer.Operation; var job = transfer.Job;
        var state = op.State;
        if (state == CoreWebView2DownloadState.Completed)
        {
            // Claim A4 before notifications can re-enter cancellation or completion.
            transfer.Finalizing = true;
            try
            {
                if (transfer.Faulted)
                {
                    changed(job, null); // Failed transfers keep their bytes; no speculative promotion.
                    return;
                }
                // A completion racing cancellation still needs the final native byte count.
                job.DownloadedBytes = op.BytesReceived;
                job.TotalBytes = op.TotalBytesToReceive is ulong total && total is > 0 and <= long.MaxValue ? (long)total : null;
                Update(transfer, true, completing: true);
                job.State = "Checking integrity"; job.CanPause = job.CanResume = false; changed(job, null);
                var item = await DownloadCompletion.CompleteAsync(job, settings, PersistNativeCompletion, CancellationToken.None);
                changed(job, item);
                TryDiscard(job);
            }
            catch (Exception ex)
            {
                if (job.Completion is null)
                {
                    job.State = ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase) ? "Integrity failed" : "Failed";
                    job.Error = "The downloaded file could not be verified or its state saved. Incomplete files were retained.";
                }
                else if (job.Completion.Phase == "Completed") job.Error = "Download completed durably, but its UI notification failed.";
                try { changed(job, null); }
                catch { job.Error += " The latest notification or state could not be saved; keep this record and retry after checking disk space and permissions."; }
            }
            finally { Detach(transfer); }
            return;
        }
        if (state == CoreWebView2DownloadState.Interrupted && op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled)
        {
            job.State = transfer.StopState ?? "Cancelled"; job.CanPause = job.CanResume = false;
            if (!transfer.Faulted) job.Error = job.State == "Interrupted" ?
                WindowClosureGuidance : CancelledGuidance;
            try { changed(job, null); }
            finally { Detach(transfer); } // Native termination, independent of notification/persistence success.
            return;
        }
        if (ShuttingDown || transfer.Faulted || transfer.StopState is not null) return;
        Update(transfer, true);
        if (!transfers.ContainsKey(job.Id) || transfer.StatePending || transfer.Faulted || transfer.StopState is not null || ShuttingDown) return;
        if (state == CoreWebView2DownloadState.InProgress)
        {
            job.State = "Downloading"; job.Error = ""; changed(job, null); return;
        }
        if (state == CoreWebView2DownloadState.Interrupted)
        {
            job.State = op.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused ? "Paused" : "Interrupted";
            job.Error = "Browser transfer: " + op.InterruptReason + ". If the provider workflow cannot continue here, use the external browser and import the matching completed file.";
            changed(job, null);
        }
    }

    public void Pause(DownloadJob job)
    {
        if (!transfers.TryGetValue(job.Id, out var transfer) || !job.CanPause) throw new InvalidOperationException("This transfer cannot be paused.");
        transfer.Operation.Pause();
    }
    public void Resume(DownloadJob job)
    {
        if (ShuttingDown || !transfers.TryGetValue(job.Id, out var transfer) || transfer.Faulted || transfer.StopState is not null || !transfer.Operation.CanResume)
            throw new InvalidOperationException("Resume is unavailable. Open the mirror again in a new browser session.");
        transfer.InitialBytes = transfer.Operation.BytesReceived; transfer.Clock.Restart(); transfer.LastNotification = 0;
        transfer.Operation.Resume();
    }
    internal async Task<bool> TryResumeToSettlementAsync(DownloadJob job)
    {
        if (!transfers.TryGetValue(job.Id, out var transfer) || !job.CanResume) return false;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, object args)
        {
            try { if (transfer.Operation.State != CoreWebView2DownloadState.InProgress) settled.TrySetResult(); }
            catch (COMException) { settled.TrySetResult(); }
        }
        transfer.Operation.StateChanged += Changed;
        try
        {
            try { Resume(job); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { return false; }
            await Task.WhenAny(settled.Task, transfer.Finished.Task);
            if (transfer.Operation.State == CoreWebView2DownloadState.Completed) await transfer.Finished.Task;
            // An explicit Pause/Cancel ends Retry without starting another transfer.
            return job.State is "Downloaded" or "Imported" or "Paused" or "Cancelled" || transfer.StopState is not null;
        }
        finally { transfer.Operation.StateChanged -= Changed; }
    }
    public void Cancel(DownloadJob job)
    {
        if (DownloadCompletion.Unresolved(job)) throw new InvalidOperationException("Finalisation evidence must be kept. Restart to recover this download.");
        if (importWriters.TryGetValue(job.Id, out var importing)) { importing.Cancellation.Cancel(); return; }
        if (transfers.TryGetValue(job.Id, out var transfer))
        {
            if (transfer.Finalizing) throw new InvalidOperationException("The completed transfer is being verified.");
            transfer.StopState = "Cancelled";
            transfer.Report("CancellationRequested");
            transfer.Operation.Cancel();
            if (transfer.Finalizing) return; // Completion may win inside the native Cancel callback.
            job.State = transfers.ContainsKey(job.Id) ? "Cancelling" : "Cancelled"; job.CanPause = job.CanResume = false;
            try { changed(job, null); } catch { CallbackFailed(transfer); }
            if (transfer.Finalizing || !WriterStopped(transfer)) return;
            Detach(transfer);
        }
        job.State = "Cancelled"; job.CanPause = job.CanResume = false;
        if (transfer is null || !transfer.Faulted) job.Error = CancelledGuidance;
        try { changed(job, null); }
        catch when (transfer is not null) { CallbackFailed(transfer); }
    }
    public async Task CancelAsync(DownloadJob job)
    {
        var finished = transfers.TryGetValue(job.Id, out var transfer) ? transfer.Finished.Task :
            importWriters.TryGetValue(job.Id, out var importing) ? importing.Finished.Task : Task.CompletedTask;
        try { Cancel(job); }
        catch (COMException) { throw new IOException("Native writer termination could not be confirmed. Keep the record and retry after the browser releases it."); }
        try { await finished.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new IOException("Writer termination is not confirmed. Keep this record and retry after the browser releases the transfer."); }
        if (IsActive(job)) throw new IOException("The download writer is still owned by this session.");
    }
    public void Interrupt(DownloadJob job)
    {
        if (!transfers.TryGetValue(job.Id, out var transfer) || transfer.Finalizing) return;
        if (transfer.StopState == "Cancelled") return;
        transfer.StopState = "Interrupted";
        transfer.Report("InterruptionRequested");
        try { transfer.Operation.Cancel(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* Retain ownership if native termination cannot be established. */ }
        if (transfer.Finalizing) return;
        job.State = "Interrupted"; job.CanPause = job.CanResume = false;
        if (!transfer.Faulted) job.Error = WindowClosureGuidance;
        try { changed(job, null); } catch { CallbackFailed(transfer); }
        try { if (!transfer.Finalizing && WriterStopped(transfer)) Detach(transfer); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* Closed native state cannot establish termination. */ }
    }
    public async Task ImportAsync(CatalogEntry entry, DownloadMirror mirror, string path, string? selectionIdentity = null)
    {
        DownloadAdmission.EnsureEnabled(entry);
        OrbitDownloadPolicy.ValidateFileName(entry, mirror.Artifact, path);
        if (Path.GetExtension(path).ToLowerInvariant() is ".crdownload" or ".part" or ".tmp") throw new IOException("Select a completed file, not a browser temporary transfer.");
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root()))!).AvailableFreeSpace < new FileInfo(path).Length)
            throw new IOException("Insufficient disk space to import this file.");
        var job = admission.Admit([selectionIdentity ?? DownloadAdmission.Capture(entry, mirror.Artifact)],
            () => { var prepared = BrowserDownloadFiles.Prepare(entry, mirror, root(), Path.GetFileName(path), jobs());
                prepared.Method = "External import"; prepared.State = "Checking integrity"; return [prepared]; },
            accepted => added(accepted[0]), rejected => BrowserDownloadFiles.DiscardStaging(rejected[0]))[0];
        imports++;
        using var cancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        importWriters.Add(job.Id, (cancellation, finished));
        try
        {
            await using (var source = File.OpenRead(path))
            await using (var target = new FileStream(job.StagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(target, cancellation.Token);
                await target.FlushAsync(cancellation.Token); target.Flush(true);
            }
            job.DownloadedBytes = new FileInfo(job.StagingPath).Length;
            var item = await DownloadCompletion.CompleteAsync(job, settings, persist, cancellation.Token);
            changed(job, item);
            TryDiscard(job);
        }
        catch (OperationCanceledException) { job.State = "Interrupted"; job.Error = "Import stopped; incomplete files retained."; changed(job, null); }
        catch (Exception ex) { if (job.Completion is null) { job.State = "Failed"; job.Error = ex.Message; } changed(job, null); throw; }
        finally { imports--; importWriters.Remove(job.Id); admission.Release(job); finished.TrySetResult(); }
    }
    private void TryDiscard(DownloadJob job)
    {
        try { BrowserDownloadFiles.DiscardStaging(job); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Error = "Download completed; its empty staging directory could not be removed."; changed(job, null); }
    }
    private static bool WriterStopped(Transfer transfer) =>
        transfer.Operation.State == CoreWebView2DownloadState.Completed ||
        (transfer.Operation.State == CoreWebView2DownloadState.Interrupted &&
         transfer.Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled);

    private void StopAfterError(Transfer transfer)
    {
        try
        {
            transfer.Operation.Cancel();
            if (!transfer.Finalizing && WriterStopped(transfer)) Detach(transfer);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* Keep ownership until termination is known. */ }
    }
    private void CallbackFailed(Transfer transfer)
    {
        if (transfer.Faulted) return; // One cancellation and one error notification, no persistence retry loop.
        transfer.Faulted = true;
        transfer.StopState ??= "Interrupted";
        var job = transfer.Job;
        if (job.Completion is null) job.State = transfer.StopState;
        job.CanPause = job.CanResume = false;
        job.Error = "Browser callback or state persistence failed. Cancellation was requested; keep the record and incomplete files until termination is confirmed.";
        if (transfers.ContainsKey(job.Id)) StopAfterError(transfer);
        try { changed(job, null); }
        catch { job.Error += " The latest state could not be saved; check disk space and permissions before retrying."; }
    }
    private void PersistNativeCompletion(AppSettings candidate)
    {
        try { persist(candidate); }
        catch { throw new IOException("Download state could not be saved. Check available disk space and permissions; keep the recovery record and files."); }
    }
    private void Detach(Transfer transfer)
    {
        if (!transfers.TryGetValue(transfer.Job.Id, out var owner) || !ReferenceEquals(owner, transfer)) return;
        try
        {
            transfer.Operation.BytesReceivedChanged -= transfer.ProgressHandler;
            transfer.Operation.EstimatedEndTimeChanged -= transfer.ProgressHandler;
            transfer.Operation.StateChanged -= transfer.StateHandler;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* A closed native controller has no usable event source. */ }
        transfers.Remove(transfer.Job.Id);
        admission.Release(transfer.Job);
        transfer.Finished.TrySetResult();
        transfer.Report("OwnerReleased");
    }
    public void Dispose()
    {
        ShuttingDown = true;
        foreach (var transfer in transfers.Values.ToArray()) Interrupt(transfer.Job);
    }
}
