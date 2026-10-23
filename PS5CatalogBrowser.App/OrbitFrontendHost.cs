using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

// This privileged, local frontend never shares a profile or bridge with provider pages.
internal sealed class OrbitFrontendHost : UserControl, IDisposable
{
    internal WebView2 Browser { get; } = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal event Action<string>? PageRequested;
    internal event Action<OrbitFrontendAction>? ActionRequested;
    internal event Action<OrbitSettingsAction>? SettingsActionRequested;
    internal Func<bool>? SourceEditorAvailable;
    internal (CatalogEntry Entry, DownloadMirror Mirror)? ResolveRelease(string id) => api?.ResolveRelease(id);
    internal event Action<bool>? FullscreenRequested;
    internal event Action? ToolbarRequested;
    internal event Action<string>? ExternalLinkRequested;
    internal event Action<string>? Failed;
    private OrbitFrontendApi? api;
    private Task? initialization;
    private bool disposed;
    private Dictionary<string, byte[]> assets = [];

    public OrbitFrontendHost() { Content = Browser; }
    internal Task InitializeAsync(MainViewModel model) => initialization ??= InitializeCoreAsync(model);
    private async Task InitializeCoreAsync(MainViewModel model)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "OrbitFrontend");
        assets = VerifyAssets(root);
        api = new(model) { SourceEditorAvailable = () => SourceEditorAvailable?.Invoke() ?? true };
        api.SettingsActionRequested += request => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!disposed && api.SettingsActions.Contains(request.Action)) SettingsActionRequested?.Invoke(request);
            else if (!disposed) model.Status = "This settings action is no longer available. Wait for the current operation to finish.";
        }));
        // Return the API acknowledgement before a picker or confirmation can run a nested native message loop.
        api.ActionRequested += action => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (disposed) return;
            if (api.Allows(action.Action, action.Id)) ActionRequested?.Invoke(action);
            else model.Status = "This action is no longer available. Refresh and select it again.";
        }));
        var environment = await CoreWebView2Environment.CreateAsync(null, model.FrontendProfilePath);
        if (disposed) return;
        await Browser.EnsureCoreWebView2Async(environment);
        var core = Browser.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
        core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.NavigationStarting += (_, e) =>
        {
            if (OrbitFrontendSecurity.IsDocument(e.Uri)) return;
            e.Cancel = true;
            if (e.IsUserInitiated && !e.IsRedirected) RequestExternal(e.Uri);
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = !IsYouTubeEmbed(e.Uri);
        core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.IsUserInitiated) RequestExternal(e.Uri); };
        core.NavigationCompleted += (_, e) =>
        { if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) Fail("Orbit could not load (" + e.WebErrorStatus + "). Use Classic Windows."); };
        core.ProcessFailed += (_, _) => Fail("The Orbit browser stopped. Use Classic Windows, or restart the app to retry.");
        core.WebMessageReceived += (_, e) =>
        {
            if (!OrbitFrontendSecurity.TryCommand(e.Source, core.Source, e.WebMessageAsJson, out var command, out var page)) return;
            switch (command)
            {
                case "ready": Ready.TrySetResult(); break;
                case "error": Fail("Orbit reported a rendering error. Use Classic Windows."); break;
                case "navigate": PageRequested?.Invoke(page!); break;
                case "fullscreen": FullscreenRequested?.Invoke(true); break;
                case "exitFullscreen": FullscreenRequested?.Invoke(false); break;
                case "toolbar": ToolbarRequested?.Invoke(); break;
            }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ResourceRequested;
        // Virtual folder mappings bypass WebResourceRequested, including Orbit's same-origin API.
        // Serve only verified bundle members and the bounded API at this private, non-listening origin.
        core.Navigate(OrbitFrontendSecurity.Document);
        await Ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    private void RequestExternal(string url)
    {
        try { ExternalLinkRequested?.Invoke(NetworkPolicy.Validate(url).AbsoluteUri); }
        catch (Exception) { /* Invalid and non-web schemes are never handed to Windows. */ }
    }
    private void Fail(string message) { Ready.TrySetException(new IOException(message)); Failed?.Invoke(message); }
    private async void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        var request = e.Request;
        if (OrbitFrontendSecurity.IsOrigin(request.Uri))
        {
            var path = new Uri(request.Uri).AbsolutePath;
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (request.Method != "GET" || !assets.TryGetValue(path, out var bytes)) { Respond(404, new { error = "Asset unavailable." }); return; }
                var mime = Path.GetExtension(path) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".svg" => "image/svg+xml", ".woff2" => "font/woff2", _ => "application/octet-stream" };
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK",
                    "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
                return;
            }
            if (path == "/api/v1/media" && request.Method == "POST")
            {
                var deferral = e.GetDeferral();
                try
                {
                    string? body = null;
                    if (request.Content is { } mediaStream)
                    {
                        using var buffer = new MemoryStream(); var chunk = new byte[1024]; int count;
                        while ((count = mediaStream.Read(chunk, 0, chunk.Length)) != 0)
                        {
                            if (buffer.Length + count > 4096) { Respond(413, new { error = "Request too large." }); return; }
                            buffer.Write(chunk, 0, count);
                        }
                        body = Encoding.UTF8.GetString(buffer.ToArray());
                    }
                    var origin = request.Headers.Contains("Origin") ? request.Headers.GetHeader("Origin") : null;
                    var result = await api!.RespondMediaAsync(core.Source, request.Method, request.Uri, body, origin);
                    Respond(result.Status, result.Body);
                }
                catch (Exception) { Respond(500, new { error = "Windows could not load game media. Try again in a moment." }); }
                finally { deferral.Complete(); }
                return;
            }
            try
            {
                string? body = null;
                if (request.Content is { } stream)
                {
                    using var buffer = new MemoryStream(); var chunk = new byte[1024]; int count;
                    while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
                    {
                        if (buffer.Length + count > (path == "/api/v1/windows/settings" ? 16384 : 4096)) { Respond(413, new { error = "Request too large." }); return; }
                        buffer.Write(chunk, 0, count);
                    }
                    body = Encoding.UTF8.GetString(buffer.ToArray());
                }
                var origin = request.Headers.Contains("Origin") ? request.Headers.GetHeader("Origin") : null;
                var result = api!.Respond(core.Source, request.Method, request.Uri, body, origin);
                Respond(result.Status, result.Body);
            }
            catch (Exception) { Respond(500, new { error = "Windows could not complete this request. Open Classic Windows." }); }
            return;
        }
        // Remote image requests and the allowlisted YouTube player are the frontend network exceptions.
        if (e.ResourceContext == CoreWebView2WebResourceContext.Image)
            try { NetworkPolicy.Validate(request.Uri); return; } catch (Exception) { }
        if (IsYouTubeResource(request.Uri, e.ResourceContext)) return;
        Respond(403, new { error = "Resource blocked." });
        void Respond(int status, object value) => e.Response = core.Environment.CreateWebResourceResponse(
            new MemoryStream(api!.SerializeResponse(value)), status, status == 200 ? "OK" : "Blocked",
            "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
    }
    private static bool IsYouTubeEmbed(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IdnHost == "www.youtube-nocookie.com" && uri.Port == 443 &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal) &&
        uri.Query.Length <= 100 && IsVideoId(uri.AbsolutePath[7..]);
    private static bool IsVideoId(string value) => value.Length is >= 6 and <= 20 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static bool IsYouTubeResource(string value, CoreWebView2WebResourceContext context)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0) return false;
        if (context == CoreWebView2WebResourceContext.Document) return IsYouTubeEmbed(value);
        if (uri.IdnHost is "www.youtube-nocookie.com" or "www.youtube.com") return true;
        if (uri.IdnHost.EndsWith(".googlevideo.com", StringComparison.Ordinal))
            return context is CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.XmlHttpRequest;
        if (uri.IdnHost.EndsWith(".ytimg.com", StringComparison.Ordinal))
            return context is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Script or CoreWebView2WebResourceContext.Stylesheet;
        return uri.IdnHost == "youtubei.googleapis.com" && context is CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.XmlHttpRequest;
    }
    internal void NotifyFullscreen(bool fullscreen)
    {
        if (!disposed && Browser.CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(new { fullscreen }));
    }
    internal void NotifyGameLaunching(string title, EmulatorBackend backend)
    {
        if (!disposed && Browser.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "launching", game = title[..Math.Min(title.Length, 500)], emulator = backend.ToString() }));
    }
    internal void NotifyGameExited(EmulatorBackend backend)
    {
        if (!disposed && Browser.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "gameExited", emulator = backend.ToString() }));
    }
    internal void ShowAppearance() => Browser.CoreWebView2?.PostWebMessageAsJson("{\"command\":\"appearance\"}");
    internal void ShowDownloadToast(string title) => Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "toast", message = "Download added: " + title[..Math.Min(title.Length, 500)] }));
    internal bool OpenDownloadStore(DownloadJob job)
    {
        if (Browser.CoreWebView2 is not { } core || api is null) return false;
        var title = api.DownloadSearchTitle(job);
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "game", id = api.StoreGameId(job), query = title[..Math.Min(title.Length, 1024)] }));
        return true;
    }
    internal void NavigatePage(string page) => Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "page", page }));
    private static Dictionary<string, byte[]> VerifyAssets(string root)
    {
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "asset-sha256.json")))
            ?? throw new IOException("Orbit asset manifest missing.");
        if (!manifest.ContainsKey("index.html")) throw new IOException("Orbit entry point missing.");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (relative, expected) in manifest)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Orbit asset path invalid.");
            ImageFileSystem.EnsureNoLinks(path);
            var bytes = File.ReadAllBytes(path);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Orbit asset verification failed.");
            result.Add("/" + relative.Replace('\\', '/'), bytes);
        }
        return result;
    }
    public void Dispose() { if (disposed) return; disposed = true; api?.Dispose(); Browser.Dispose(); }
}
