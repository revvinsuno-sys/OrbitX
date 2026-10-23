using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public partial class BrowserDownloadWindow : Window
{
    public static string ProfilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS5CatalogBrowser", "browser-profile");
    private readonly CatalogEntry entry;
    private readonly DownloadMirror mirror;
    private readonly BrowserDownloadManager manager;
    private readonly string profile;
    private readonly string selectionIdentity;
    private readonly Func<string, bool> confirm;
    private readonly List<DownloadJob> accepted = [];
    private int blockedPopups;
    private int pendingOffers;
    private readonly CancellationTokenSource browserLifetime = new();
    private bool closeAfterOffer;
    private readonly Action? unavailable;
    private readonly DownloadJob? queuedJob;
    private bool refreshQueuedPage;
    private bool clickQueuedDownload;
    private bool fallbackRequested;
    internal ProviderFlowDiagnostics Diagnostics { get; }
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserDownloadWindow(CatalogEntry entry, DownloadMirror mirror, BrowserDownloadManager manager,
        string? profile = null, Func<string, bool>? confirm = null, bool diagnostics = false, Action? unavailable = null, DownloadJob? queuedJob = null)
    {
        DownloadAdmission.EnsureEnabled(entry);
        OrbitDownloadPolicy.ValidateSelection(entry, mirror.Artifact);
        // Freeze the selected release across refresh and native callbacks, without copying secrets.
        if (entry.CatalogFormat == "Orbit")
        {
            entry = System.Text.Json.JsonSerializer.Deserialize<CatalogEntry>(System.Text.Json.JsonSerializer.Serialize(entry))!;
            var artifact = entry.Artifacts.Single(a => a.Id == mirror.Artifact.Id);
            mirror = DownloadMirror.ForEntry(entry).Single(m => m.Artifact == artifact);
        }
        selectionIdentity = DownloadAdmission.Capture(entry, mirror.Artifact);
        Diagnostics = new(selectionIdentity) { Enabled = diagnostics };
        InitializeComponent();
        EnableDiagnostics.IsChecked = diagnostics;
        this.entry = entry; this.mirror = mirror; this.manager = manager; this.profile = profile ?? ProfilePath;
        this.unavailable = unavailable;
        this.queuedJob = queuedJob;
        refreshQueuedPage = queuedJob is not null;
        if (queuedJob is not null) accepted.Add(queuedJob);
        ChooseAnother.Visibility = unavailable is null ? Visibility.Collapsed : Visibility.Visible;
        var session = new BrowserDownloadManager.ProviderSession(Guid.NewGuid().ToString("N"), entry.Title, accepted, () =>
        { WindowState = WindowState.Normal; Show(); Activate(); }, () => WindowState = WindowState.Minimized);
        var initialized = false;
        Loaded += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            manager.Sessions.Add(session);
            await InitializeAsync();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            // Finish the native minimise transition before hiding, or Windows can show it again.
            _ = Dispatcher.BeginInvoke(new Action(() => { if (WindowState == WindowState.Minimized) HideProviderWindow(); }));
        };
        this.confirm = confirm ?? (message => MessageBox.Show(this, message, "Confirm browser download", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        SelectionLabel.Text = entry.Title + " · " + mirror.Provider + " · " + mirror.ExpectedFileName + " · " + mirror.SizeLabel;
        WorkflowGuidance.Text = mirror.BrowserInstructions + " Opening a page does not start a transfer. If the site cannot offer the file here, use external browser/import.";
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Diagnostics.Record("WindowClosed");
            browserLifetime.Cancel();
            if (Browser.CoreWebView2 is { } core) manager.Bandwidth?.Detach(core);
            Browser.Dispose(); browserLifetime.Dispose();
            manager.ProviderWindowClosed(session);
        };
    }

    private void HideProviderWindow()
    {
        Hide();
        if (Owner is { IsLoaded: true } owner)
        {
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = owner.WindowStyle == WindowStyle.None ? WindowState.Maximized : WindowState.Normal;
            owner.Activate();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                Diagnostics.Record("NavigationStarting", e.Uri, e.IsUserInitiated, navigationId: e.NavigationId);
                try { NetworkPolicy.Validate(e.Uri); }
                catch { e.Cancel = true; Diagnostics.Record("NavigationBlocked", e.Uri, decision: "blocked"); PageStatus.Text = "Navigation blocked: use HTTPS or a local development page."; }
            };
            core.NavigationCompleted += (_, e) =>
            {
                Diagnostics.Record("NavigationCompleted", core.Source, state: e.IsSuccess ? "Success" : e.WebErrorStatus.ToString(), navigationId: e.NavigationId);
                NavigationCompleted(e.IsSuccess, e.WebErrorStatus, core.Source);
                if (e.IsSuccess && refreshQueuedPage)
                {
                    refreshQueuedPage = false;
                    clickQueuedDownload = true;
                    PageStatus.Text = "Queued game opened. Refreshing the provider page before attempting its Download button.";
                    core.Reload();
                    return;
                }
                if (e.IsSuccess && clickQueuedDownload)
                {
                    clickQueuedDownload = false;
                    _ = AttemptQueuedDownloadClickAsync(core);
                }
                // A navigation cancelled by a native file offer is normal. Only a provider HTTP error requests another release.
                if (e.HttpStatusCode >= 400) RequestFreshRelease();
            };
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "data" or "blob" or "about") return;
                try { NetworkPolicy.Validate(e.Request.Uri); }
                catch { e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain"); }
            };
            core.NewWindowRequested += (_, e) =>
            {
                Diagnostics.Record("NewWindowRequested", e.Uri, e.IsUserInitiated);
                e.Handled = true;
                var deferral = e.GetDeferral();
                try
                {
                    var uri = NetworkPolicy.Validate(e.Uri);
                    if (BlockPopups.IsChecked == true)
                    {
                        Diagnostics.Record("PopupDecision", e.Uri, e.IsUserInitiated, "blocked-by-setting-unverified-target");
                        PopupStatus.Text = $"Blocked {++blockedPopups} popup(s). The download page is still open.";
                        return;
                    }
                    if (e.IsUserInitiated && confirm("Open the requested popup in this window?\nWebsite: " + uri.Host))
                    {
                        Diagnostics.Record("PopupDecision", e.Uri, true, "confirmed-current-window");
                        core.Navigate(uri.AbsoluteUri);
                    }
                    else
                    {
                        Diagnostics.Record("PopupDecision", e.Uri, e.IsUserInitiated, "blocked-no-confirmed-user-navigation");
                        PageStatus.Text = "Popup blocked. Use the external browser if this site needs a separate login window.";
                    }
                }
                catch { Diagnostics.Record("PopupDecision", e.Uri, e.IsUserInitiated, "blocked-unsafe-or-failed"); PageStatus.Text = "Unsafe popup blocked."; }
                finally { deferral.Complete(); }
            };
            core.DownloadStarting += DownloadStarting;
            ConfigureAkiraBox();
            if (manager.Bandwidth is { } bandwidth)
            {
                PageStatus.Text = "Waiting for the shared download slot. Finish, resume or cancel the current transfer, or close its provider window.";
                await bandwidth.AttachAsync(core, browserLifetime.Token);
            }
            core.Navigate(NetworkPolicy.Validate(mirror.BrowserUrl).AbsoluteUri);
            Ready.TrySetResult();
        }
        catch
        {
            PageStatus.Text = "Embedded browser could not start. Install the Microsoft Edge WebView2 Runtime, or use the external browser and import the completed file.";
            Ready.TrySetException(new InvalidOperationException(PageStatus.Text));
        }
    }

    private async Task AttemptQueuedDownloadClickAsync(CoreWebView2 core)
    {
        try
        {
            await Task.Delay(750, browserLifetime.Token);
            var result = await core.ExecuteScriptAsync("(() => { const visible = e => { const r=e.getBoundingClientRect(), s=getComputedStyle(e); return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none'; }; const label=e => (e.innerText || e.value || e.getAttribute('aria-label') || '').replace(/\\s+/g,' ').trim(); const matches=[...document.querySelectorAll('button,a,[role=button],input[type=button],input[type=submit]')].filter(e => visible(e) && /^download(?:\\s+(?:now|file))?$/i.test(label(e))); if(matches.length!==1) return matches.length ? 'ambiguous' : 'missing'; matches[0].click(); return 'clicked'; })()");
            var outcome = System.Text.Json.JsonSerializer.Deserialize<string>(result);
            PageStatus.Text = outcome switch
            {
                "clicked" => "Refreshed the queued provider page and attempted its Download button. Confirm any verification and review the offered file.",
                "ambiguous" => "Provider page refreshed, but multiple Download buttons were found. Choose the correct one manually.",
                _ => "Provider page refreshed, but no clear Download button was found. Continue manually."
            };
        }
        catch (OperationCanceledException) { }
        catch { PageStatus.Text = "Provider page refreshed. Automatic button detection was unavailable; continue manually."; }
    }

    private void ChooseAnother_Click(object sender, RoutedEventArgs e) => RequestFreshRelease();
    internal void RequestFreshRelease()
    {
        if (unavailable is null || fallbackRequested || accepted.Count > 0 || pendingOffers > 0) return;
        fallbackRequested = true;
        _ = Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) { Close(); unavailable(); } }));
    }

    private async void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; e.Cancel = true;
        var deferral = e.GetDeferral();
        pendingOffers++;
        try
        {
            Diagnostics.Record("DownloadStarting", e.DownloadOperation.Uri, state: "AwaitingConfirmation");
            var host = NetworkPolicy.Validate(e.DownloadOperation.Uri).Host;
            var filename = BrowserDownloadFiles.SafeName(e.ResultFilePath);
            var expected = mirror.ExpectedFileName.Length > 0 ? mirror.ExpectedFileName : "Not provided by catalogue";
            PageStatus.Text = "A native download offer is awaiting your confirmation.";
            var matches = mirror.ExpectedFileName.Length > 0 && string.Equals(Path.GetFileName(e.ResultFilePath), mirror.ExpectedFileName, StringComparison.Ordinal);
            if (!matches && !confirm($"Accept this download for {entry.Title}?\n\nWebsite: {host}\nOffered file: {filename}\nExpected file: {expected}\n\nOnly accept content you intended to download. Size and checksum metadata will still be checked."))
            {
                Diagnostics.Record("OfferDecision", decision: "declined");
                PageStatus.Text = "Download offer declined. No new transfer was admitted.";
                return;
            }
            if (manager.Bandwidth is { } bandwidth) await bandwidth.BeginBrowserAsync(Browser.CoreWebView2, browserLifetime.Token);
            var job = AcceptNativeDownload(filename, new NativeDownloadOperation(e.DownloadOperation));
            e.ResultFilePath = job.StagingPath;
            e.Cancel = false;
            Diagnostics.Record("OfferDecision", decision: "admitted", state: job.State);
            PageStatus.Text = "Download accepted. Track it on the Downloads page; keep this window open while transferring.";
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded) return;
                HideProviderWindow();
            }));
        }
        catch (Exception ex)
        {
            if (!accepted.Any(manager.IsActive)) manager.Bandwidth?.EndBrowser(Browser.CoreWebView2);
            Diagnostics.Record("OfferDecision", decision: "rejected");
            PageStatus.Text = ex is IOException or InvalidDataException ? ex.Message : "Download was rejected. The URL, destination or bandwidth configuration is unavailable.";
        }
        finally
        {
            pendingOffers--;
            try { deferral.Complete(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* The owning app can close during async admission. */ }
            if (closeAfterOffer && pendingOffers == 0) _ = Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Close(); }));
        }
    }

    internal DownloadJob AcceptNativeDownload(string filename, INativeDownloadOperation operation) =>
        manager.Accept(entry, mirror, filename, operation, selectionIdentity, job =>
        {
            if (!accepted.Contains(job)) accepted.Add(job);
            Diagnostics.Record("ApplicationAdmission", state: job.State);
        }, (job, phase, nativeState) =>
        {
            Diagnostics.Record(phase, state: nativeState ?? job.State);
            if (phase == "OwnerReleased" && !accepted.Any(manager.IsActive)) manager.Bandwidth?.EndBrowser(Browser.CoreWebView2);
            if (phase != "NativeStateChanged") PageStatus.Text = "Browser transfer status: " + job.State + ". See Downloads for details.";
        });

    internal void NavigationCompleted(bool success, CoreWebView2WebErrorStatus error, string source)
    {
        var latest = accepted.LastOrDefault();
        if (pendingOffers > 0) PageStatus.Text = "A native download offer is awaiting your confirmation. Website navigation does not determine transfer status.";
        else if (latest is not null) PageStatus.Text = "Browser transfer status: " + latest.State + ". See Downloads for details. " +
            (success ? "Website navigation completed." : "Website navigation ended: " + error + ".");
        else PageStatus.Text = success ? "Website: " + (Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : "") :
            "Website could not load: " + error + ". Use the external browser and import the matching completed file if this page cannot load here.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Diagnostics.Record("WindowClosing");
        if (pendingOffers > 0) { browserLifetime.Cancel(); closeAfterOffer = true; e.Cancel = true; PageStatus.Text = "Stopping the pending file offer before closing."; return; }
        var active = accepted.Where(manager.IsActive).ToArray();
        if (active.Length == 0) return;
        if (manager.IsVerifying) { e.Cancel = true; PageStatus.Text = "Wait for file verification to finish before closing."; return; }
        if (!manager.ShuttingDown && !confirm("Closing this provider webpage interrupts its active WebView2 transfers. Their records remain, but partial bytes may be unavailable. Close it?")) { e.Cancel = true; return; }
        foreach (var job in active) { Diagnostics.Record("WindowCancellationRequested", state: job.State); manager.Interrupt(job); }
        if (accepted.Any(manager.IsActive))
        {
            e.Cancel = true;
            PageStatus.Text = "Waiting for native download termination. Keep this window open, then retry closing.";
        }
    }
    private void Diagnostics_Changed(object sender, RoutedEventArgs e)
    {
        Diagnostics.Enabled = EnableDiagnostics.IsChecked == true;
        Diagnostics.Record("DiagnosticsEnabled");
    }
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "Export sanitised provider diagnostics", Filter = "JSON|*.json", FileName = "provider-flow.json" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, Diagnostics.Export()); }
        catch { PageStatus.Text = "Could not export provider diagnostics."; }
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoBack == true) Browser.GoBack(); }
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not { } core) return;
        if (AkiraBoxLink.FileId(mirror.BrowserUrl) is not null) core.Navigate(mirror.BrowserUrl);
        else Browser.Reload();
    }
    private void External_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(NetworkPolicy.Validate(mirror.BrowserUrl).AbsoluteUri) { UseShellExecute = true });
            PageStatus.Text = "External provider webpage opened. Complete the download there, then import the matching completed file here. Cancel or abandon that workflow in the external browser; this app cannot track or stop its transfers.";
        }
        catch { PageStatus.Text = "Could not open the external browser."; }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Select the completed external download", Filter = "All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try { await manager.ImportAsync(entry, mirror, picker.FileName, selectionIdentity); PageStatus.Text = "File verified and imported into My Library."; }
        catch (Exception ex) { PageStatus.Text = ex is IOException ? ex.Message : "Import failed. See the download record for details."; }
    }

    public static async Task ClearSiteDataAsync(Window owner)
    {
        using var browser = new WebView2();
        var window = new Window { Owner = owner, Title = "Clearing browser site data", Width = 420, Height = 150, Content = browser, ShowInTaskbar = false };
        window.Show();
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfilePath);
            await browser.EnsureCoreWebView2Async(environment);
            await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        finally { window.Close(); }
    }
}
