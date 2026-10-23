using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class OrbitInstallationFlowTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitInstallE2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindow? main = null;
        try
        {
            var rar = Convert.FromBase64String(PackageArchiveTests.Rar5);
            var files = new Dictionary<string, byte[]> { ["direct.rar"] = rar, ["browser.rar"] = rar };
            foreach (var ps5 in new[] { false, true })
            {
                var name = ps5 ? "ps5.pkg" : "ps4.pkg";
                var package = Path.Combine(root, name); PackageArchiveTests.BuildPackage(package, ps5);
                files.Add(name, File.ReadAllBytes(package)); File.Delete(package);
            }
            using var server = new FixtureServer(files);
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Interface = "Orbit Store", Sources = [new() { Id = "e2e", Name = "Synthetic end-to-end", Url = server.Url("catalog") }],
                DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = Path.Combine(root, "installed") });
            main = new(store) { ShowActivated = false }; main.Show();
            var model = (MainViewModel)main.DataContext;
            var surface = (ContentControl)main.FindName("OrbitSurface");
            var classic = (TabControl)main.FindName("ClassicTabs");
            await Wait(() => Task.FromResult(surface.Content is OrbitFrontendHost && model.Entries.Count == 4), "HTTP catalogue loaded");
            var host = (OrbitFrontendHost)surface.Content; await host.InitializeAsync(model);
            var exposedClassic = false;
            classic.IsVisibleChanged += (_, _) => exposedClassic |= classic.IsVisible;
            var installedIds = new List<string>();
            foreach (var (name, payload) in files)
            {
                model.RetainOriginalImage = name is not ("browser.rar" or "ps5.pkg");
                await Click("[aria-label='Back']", optional: true);
                await ClickText("Browse");
                using var api = new OrbitFrontendApi(model);
                var entryId = model.Entries.Single(entry => entry.Title == "E2E " + name).Id;
                var release = api.Catalog.Releases.Single(option => option.WindowsIdentity.EntryId == entryId);
                await Click(".game-tile[data-game='" + release.GameId + "']");
                await Click(".screen-details .hub-primary");
                await Click("[role=dialog] .sheet-primary");
                var browser = name == "browser.rar";
                await Click("[aria-label='Download release'] [data-windows-action='" + (browser ? "browser" : "download") + "']");
                if (browser)
                {
                    await Wait(() => Task.FromResult(main.OwnedWindows.OfType<BrowserDownloadWindow>().Any()), "native provider browser opened from Orbit");
                    var provider = main.OwnedWindows.OfType<BrowserDownloadWindow>().Single();
                    await provider.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    provider.Close();
                    var selectedEntry = model.Entries.Single(entry => entry.Id == entryId);
                    var selectedMirror = DownloadMirror.ForEntry(selectedEntry).Single();
                    var failed = BrowserDownloadFiles.Prepare(selectedEntry, selectedMirror, model.DownloadRoot, name, model.Downloads);
                    failed.SelectionIdentity = DownloadAdmission.Capture(selectedEntry, selectedMirror.Artifact);
                    BrowserDownloadFiles.DiscardStaging(failed); failed.State = "Interrupted";
                    model.Settings.Downloads.Add(failed); model.Downloads.Add(failed); store.Save(model.Settings);
                    host.NavigatePage("Downloads");
                    await Click("[data-windows-id='" + failed.Id + "'] [data-windows-action=retryDownload]");
                    await Wait(() => Task.FromResult(main.OwnedWindows.OfType<BrowserDownloadWindow>().Any()), "Retry opens saved provider URL before store fallback");
                    provider = main.OwnedWindows.OfType<BrowserDownloadWindow>().Single();
                    await provider.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await Wait(async () => await provider.Browser.ExecuteScriptAsync("!!document.querySelector('#download')") == "true", "provider landing link loaded");
                    var session = model.BrowserDownloads.Sessions.Single();
                    provider.WindowState = WindowState.Minimized;
                    await Wait(() => Task.FromResult(!provider.IsVisible), "minimising hides the waiting provider window completely");
                    host.NavigatePage("Downloads");
                    await Click("[data-windows-id='" + session.Id + "'] [data-windows-action=checkStatus]");
                    await Wait(() => Task.FromResult(provider.IsVisible && provider.WindowState == WindowState.Normal), "Check status restores waiting provider window");
                    Check(model.BrowserDownloads.Sessions.Count == 1, "restoring a hidden provider preserves a single session");
                    Check(model.Downloads.Count(job => job.ArtifactId == release.WindowsIdentity.ArtifactId) == 1 && model.Downloads.Contains(failed), "pending browser session creates no extra download record");
                    await ClickText("Browse");
                    await Click(".game-tile[data-game='" + release.GameId + "']");
                    using var confirmation = new BrowserConfirmation(provider);
                    await provider.Browser.ExecuteScriptAsync("document.querySelector('#download').click()");
                    await Wait(() => Task.FromResult(model.Downloads.Any(job => job.ArtifactId == release.WindowsIdentity.ArtifactId && job.State == "Downloaded")), "native browser download completed");
                    Check(confirmation.Count == 0 && !provider.Browser.CoreWebView2.Settings.IsWebMessageEnabled, "matching browser offer accepted automatically in isolated provider window");
                    await Wait(() => Task.FromResult(!provider.IsVisible), "accepted provider window hidden without closing its writer");
                    var captured = model.Downloads.Single(job => job.ArtifactId == release.WindowsIdentity.ArtifactId);
                    Check(failed.IsCleared && captured.Id != failed.Id, "retried native offer replaces failed record after admission");
                    Check(store.Load().Downloads.Single(job => job.Id == captured.Id).Url == server.Url("provider"), "original provider page persisted, not the native transfer URL");
                    host.NavigatePage("Downloads");
                    await Click("[data-windows-id='" + captured.Id + "'] [data-windows-action=checkStatus]");
                    await Wait(() => Task.FromResult(provider.IsVisible && provider.WindowState == WindowState.Normal), "Check status restores accepted download window");
                    provider.Close();
                    Check(!model.BrowserDownloads.CanCheckStatus(captured.Id), "closed window no longer exposes Check status");
                    await ClickText("Browse");
                    await Click(".game-tile[data-game='" + release.GameId + "']");
                }
                await Wait(() => Task.FromResult(model.Downloads.Any(job => job.ArtifactId == release.WindowsIdentity.ArtifactId && job.State == "Downloaded")), "HTTP file downloaded and verified");
                var download = model.Downloads.Single(job => job.ArtifactId == release.WindowsIdentity.ArtifactId);
                Check(File.ReadAllBytes(download.Destination).SequenceEqual(payload) && download.ActualSha256.Equals(Convert.ToHexString(SHA256.HashData(payload)), StringComparison.OrdinalIgnoreCase), "download bytes and digest match selected release");
                Check(store.Load().Downloads.Single(job => job.Id == download.Id).Completion?.Phase == "Completed", "durable completion before installation");
                Check(await host.Browser.ExecuteScriptAsync("!!document.querySelector('.screen-details') && !document.querySelector('[aria-label=\"Downloads\"].windows-management')") == "true", "adding a download preserves the current game screen");
                await Wait(async () => (await host.Browser.ExecuteScriptAsync("document.querySelector('.windows-toast')?.textContent || ''")).Contains("Download added:"), "download-added toast in Orbit");
                host.NavigatePage("Downloads");
                await Capture(name + "-download", download.Id);
                var extractionProgressMatched = false;
                void CheckExtractionProgress(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName != nameof(MainViewModel.IsExtracting) || !model.IsExtracting) return;
                    var response = api.Respond(OrbitFrontendSecurity.Origin + "/index.html", "GET", OrbitFrontendSecurity.Origin + "/api/v1/windows/state");
                    using var state = JsonDocument.Parse(api.SerializeResponse(response.Body));
                    var cards = state.RootElement.GetProperty("installations").EnumerateArray().ToArray();
                    extractionProgressMatched = cards.Single(card => card.GetProperty("cardId").GetString() == download.Id).GetProperty("progress").GetProperty("kind").GetString() == "Extraction" &&
                        cards.Where(card => card.GetProperty("cardId").GetString() != download.Id).All(card => card.GetProperty("progress").ValueKind == JsonValueKind.Null);
                }
                model.PropertyChanged += CheckExtractionProgress;
                await Click("[data-windows-id='" + download.Id + "'] [data-windows-action=installDownload]");
                LocalLibraryEntry? installed = null;
                await Wait(() =>
                {
                    installed = model.LibraryView.Cast<LocalLibraryEntry>().SingleOrDefault(item => item.CatalogEntryId == release.WindowsIdentity.EntryId);
                    return Task.FromResult(installed is not null && !model.IsInstalling && !model.IsExtracting && !model.IsMovingContent &&
                        (!name.EndsWith(".rar", StringComparison.Ordinal) || download.IsCleared == !model.RetainOriginalImage));
                }, "download installed in native My Library");
                model.PropertyChanged -= CheckExtractionProgress;
                if (name.EndsWith(".rar", StringComparison.Ordinal)) Check(extractionProgressMatched, "extraction progress is attached only to its recorded download card");
                Check(installed is not null && ImageFileSystem.IsWithin(model.InstalledGamesRoot, installed.ContentPath) && Directory.Exists(installed.ContentPath), "installed files live under configured Installed directory");
                var persisted = store.Load().Library.Single(item => item.Id == installed!.Id);
                Check(persisted.ContentPath == installed!.ContentPath && File.Exists(download.Destination) == model.RetainOriginalImage, "registration persists and Settings original retention is honoured without a confirmation dialog");
                if (name.EndsWith(".pkg", StringComparison.Ordinal))
                    Check(model.InstallationPercentage == 100 && !main.OwnedWindows.Cast<Window>().Any(), "package installs without a modal and reports completed progress");
                if (name.EndsWith(".rar", StringComparison.Ordinal) && !model.RetainOriginalImage)
                {
                    await Wait(() => Task.FromResult(!model.Downloads.Contains(download)), "installed archive leaves Downloads with either retention setting");
                    await Wait(async () => await host.Browser.ExecuteScriptAsync("!document.querySelector('[aria-label=Downloads] [data-windows-id=\"" + download.Id + "\"]')") == "true", "installed archive row disappears from Orbit Downloads");
                }
                var manifest = installed.ContentMove?.Files ?? model.Installations.Single(job => job.Id == installed.InstalledGame!.InstallationId).Files;
                foreach (var file in manifest)
                    Check((await ImageFileSystem.HashAsync(Path.Combine(installed.ContentPath, file.RelativePath), default)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase), "published installed file matches verification manifest");
                await ClickText("Library");
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[aria-label=\"My Library\"] [data-windows-id=\"" + installed.Id + "\"]')") == "true", "installed entry appears in Orbit My Library");
                Check(surface.IsVisible && model.Interface == "Orbit Store" && !classic.IsVisible && !exposedClassic, "entire user flow stayed in Orbit");
                installedIds.Add(installed.Id); await Capture(name + "-library", installed.Id);
                Console.WriteLine("PASS Orbit card → " + (browser ? "provider browser" : "HTTP download") + " → " + name + " installation → visible/persisted My Library; verified files, selected source retention, no Classic");
            }
            Check(server.Requests.GetValueOrDefault("/catalog") > 0 && files.Keys.All(name => server.Requests.GetValueOrDefault("/" + name) > 0), "all payloads travelled through actual HTTP requests");
            // Hold a synthetic preparation stage so the live progress control can be inspected.
            host.NavigatePage("Downloads");
            var prepared = new TaskCompletionSource();
            var releasePreparation = new TaskCompletionSource<InstallationChoice?>();
            var progressInstall = model.InstallImageAsync(model.Downloads.Single(job => job.FileName == "ps4.pkg").Destination, confirmation =>
            {
                prepared.SetResult(); return releasePreparation.Task;
            });
            try
            {
                await prepared.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var progressDownload = model.Downloads.Single(job => job.FileName == "ps4.pkg");
                await Wait(async () => await host.Browser.ExecuteScriptAsync("(()=>{const card=document.querySelector('[data-windows-id=\"" + progressDownload.Id + "\"]');return !!card?.querySelector('[aria-label=\"Active installation\"]') && card.querySelectorAll('progress').length === 1 && !!card.querySelector('progress[aria-label=\"Installation progress\"]') && document.querySelectorAll('progress[aria-label=\"Installation progress\"]').length === 1})()") == "true", "current installation replaces download progress with exactly one bar in its game card");
            }
            finally { releasePreparation.TrySetResult(null); await progressInstall; }
            var failedDownload = model.Downloads.Single(job => job.FileName == "ps4.pkg");
            var overflowFailure = new InstallationJob { Id = "00000000000000000000000000000001", SourcePath = failedDownload.Destination, Title = failedDownload.Title, State = InstallationState.Failed, Error = "Arithmetic operation resulted in an overflow." };
            new InstallationJournal(Path.Combine(store.DirectoryPath, "installations")).Save(overflowFailure);
            model.Installations.Add(overflowFailure);
            await Wait(async () => await host.Browser.ExecuteScriptAsync("(()=>{const card=document.querySelector('[data-windows-id=\"" + failedDownload.Id + "\"]');return card?.querySelector('.windows-badge-error')?.textContent.includes('arithmetic overflow') && card.textContent.includes('outside its supported range') && card.querySelectorAll('progress').length === 0})()") == "true", "red overflow badge and explanation attached to the failed download card");
            Console.WriteLine("PASS Orbit active installation progress is visible without a modal");
            server.ProviderUnavailable = true;
            var retryEntry = model.Entries.Single(entry => entry.Title == "E2E browser.rar");
            var retryMirror = DownloadMirror.ForEntry(retryEntry).Single();
            var expired = BrowserDownloadFiles.Prepare(retryEntry, retryMirror, model.DownloadRoot, "browser.rar", model.Downloads);
            expired.SelectionIdentity = DownloadAdmission.Capture(retryEntry, retryMirror.Artifact);
            BrowserDownloadFiles.DiscardStaging(expired); expired.State = "Interrupted";
            model.Settings.Downloads.Add(expired); model.Downloads.Add(expired); store.Save(model.Settings);
            var previousRequests = server.Requests.GetValueOrDefault("/provider");
            host.NavigatePage("Downloads");
            await Click("[data-windows-id='" + expired.Id + "'] [data-windows-action=retryDownload]");
            using var retryApi = new OrbitFrontendApi(model);
            await Wait(async () => server.Requests.GetValueOrDefault("/provider") > previousRequests &&
                await host.Browser.ExecuteScriptAsync("!!document.querySelector('.screen-details') && document.querySelector('.app')?.dataset.game === " + JsonSerializer.Serialize(retryApi.StoreGameId(expired))) == "true", "expired saved provider URL returns to exact store item");
            Check(!expired.IsCleared && !main.OwnedWindows.OfType<BrowserDownloadWindow>().Any(), "expired retry preserves failed record until a new offer is accepted and closes failed provider window");
            model.Settings.Downloads.Remove(expired); model.Downloads.Remove(expired); store.Save(model.Settings);
            Console.WriteLine("PASS provider sessions: restore before/after admission, saved-page Retry capture/replacement, HTTP expiry returns to exact store item");
            main.Close(); main = null;
            using var restarted = new MainViewModel(store);
            Check(installedIds.Order().SequenceEqual(restarted.LibraryView.Cast<LocalLibraryEntry>().Select(item => item.Id).Order()), "all four installed records survive restart");
            using var restartedApi = new OrbitFrontendApi(restarted);
            var restartedState = restartedApi.Respond(OrbitFrontendSecurity.Document, "GET", OrbitFrontendSecurity.Origin + "/api/v1/windows/state");
            using var restartJson = JsonDocument.Parse(restartedApi.SerializeResponse(restartedState.Body));
            Check(restartJson.RootElement.GetProperty("installations").EnumerateArray().Single(job => job.GetProperty("cardId").GetString() == failedDownload.Id)
                .GetProperty("failure").GetProperty("label").GetString()!.Contains("arithmetic overflow"), "failure badge survives restart from the recorded installation journal");

            async Task Click(string selector, bool optional = false)
            {
                if (optional && await host.Browser.ExecuteScriptAsync("!!document.querySelector(" + JsonSerializer.Serialize(selector) + ")") != "true") return;
                await Wait(async () => await host.Browser.ExecuteScriptAsync("(()=>{const button=document.querySelector(" + JsonSerializer.Serialize(selector) + ");return !!button&&!button.disabled})()") == "true", "button " + selector);
                await host.Browser.ExecuteScriptAsync("document.querySelector(" + JsonSerializer.Serialize(selector) + ").click()");
            }
            async Task ClickText(string label)
            {
                var query = "[...document.querySelectorAll('button')].find(button=>button.textContent.trim()===" + JsonSerializer.Serialize(label) + ")";
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!(" + query + ")") == "true", "navigation " + label);
                await host.Browser.ExecuteScriptAsync("(" + query + ").click()");
            }
            async Task Capture(string label, string id)
            {
                var evidence = Environment.GetEnvironmentVariable("PS5_ORBIT_MANAGEMENT_EVIDENCE");
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence);
                await host.Browser.ExecuteScriptAsync("document.querySelector('[data-windows-id=\"" + id + "\"]')?.scrollIntoView({block:'center'})");
                await Task.Delay(150);
                using var output = File.Create(Path.Combine(evidence, "e2e-" + label + ".png"));
                await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            }
        }
        catch
        {
            if (main is not null)
                Console.Error.WriteLine(JsonSerializer.Serialize(main.OwnedWindows.OfType<BrowserDownloadWindow>().Select(window => new { window.IsLoaded, window.IsVisible, window.WindowState })));
            if (main?.DataContext is MainViewModel failed)
                Console.Error.WriteLine(JsonSerializer.Serialize(new { failed.Status, failed.ExtractionFeedback,
                    downloads = failed.Downloads.Select(job => new { job.Title, job.State, job.Error }),
                    library = failed.Library.Select(item => new { item.Title, item.CatalogEntryId, item.Classification, item.Extraction, item.ContentPath, item.ContentMove }) }));
            throw;
        }
        finally
        {
            if (main is not null)
            {
                foreach (Window owned in main.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
                await ((MainViewModel)main.DataContext).StopDirectDownloadsAsync(); main.Close();
            }
            for (var attempt = 0; attempt < 40 && Directory.Exists(root); attempt++)
                try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
            if (Directory.Exists(root)) throw new IOException("End-to-end fixture remains in use: " + root);
        }
    }
    private static async Task Wait(Func<Task<bool>> predicate, string step)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline) { if (await predicate()) return; await Task.Delay(75); }
        throw new TimeoutException("Orbit end-to-end: " + step);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    // Operate only the fixture provider window's real confirmation, never another application's dialog.
    private sealed class BrowserConfirmation : IDisposable
    {
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        public int Count { get; private set; }
        public BrowserConfirmation(Window owner)
        {
            var handle = new WindowInteropHelper(owner).Handle;
            timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
            {
                var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
                if (GetWindow(window, 4) == handle && title.ToString() == "Confirm browser download")
                { Count++; SendMessage(window, 0x111, 6, 0); }
                return true;
            }, 0);
            timer.Start();
        }
        public void Dispose() => timer.Stop();
        private delegate bool WindowCallback(nint window, nint parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
        [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    }
    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Dictionary<string, byte[]> files;
        private readonly int port;
        public ConcurrentDictionary<string, int> Requests { get; } = new();
        public volatile bool ProviderUnavailable;
        public FixtureServer(Dictionary<string, byte[]> files)
        {
            this.files = files;
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            listener.Prefixes.Add(Url("")); listener.Start(); _ = Serve();
        }
        public string Url(string path) => $"http://127.0.0.1:{port}/{path}";
        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (HttpListenerException) { return; } catch (ObjectDisposedException) { return; }
                try
                {
                    var path = context.Request.Url!.AbsolutePath; Requests.AddOrUpdate(path, 1, (_, count) => count + 1);
                    context.Response.Headers["Cache-Control"] = "no-store";
                    byte[] payload;
                    if (path == "/catalog")
                    {
                        payload = JsonSerializer.SerializeToUtf8Bytes(new { entries = files.Select(file => new { id = file.Key, title = "E2E " + file.Key, platform = file.Key == "ps4.pkg" ? "PS4" : "PS5",
                            files = new[] { new { name = file.Key, url = Url(file.Key == "browser.rar" ? "provider" : file.Key), size = file.Value.Length,
                                format = file.Key.EndsWith(".pkg", StringComparison.Ordinal) ? "PKG" : "Game folder", sha256 = Convert.ToHexString(SHA256.HashData(file.Value)) } } }) });
                        context.Response.ContentType = "application/json";
                    }
                    else if (path == "/provider")
                    { if (ProviderUnavailable) { context.Response.StatusCode = 410; context.Response.Close(); continue; }
                        payload = Encoding.UTF8.GetBytes("<html><body><a id='download' href='/browser.rar'>Download synthetic RAR</a></body></html>"); context.Response.ContentType = "text/html"; }
                    else if (files.TryGetValue(path.TrimStart('/'), out var content))
                    { payload = content; context.Response.ContentType = "application/octet-stream"; context.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + path.TrimStart('/') + "\""; }
                    else { context.Response.StatusCode = 404; context.Response.Close(); continue; }
                    context.Response.ContentLength64 = payload.Length; await context.Response.OutputStream.WriteAsync(payload); context.Response.Close();
                }
                catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException) { context.Response.Abort(); }
            }
        }
        public void Dispose() { listener.Stop(); listener.Close(); }
    }
}
