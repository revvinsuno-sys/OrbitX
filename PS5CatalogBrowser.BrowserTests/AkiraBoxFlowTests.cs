using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class AkiraBoxFlowTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5AkiraFlow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        BrowserDownloadWindow? window = null;
        try
        {
            foreach (var unavailable in new[] { false, true })
            {
                const string page = "https://akirabox.to/Fixture123/file";
                const string ready = "https://akirabox.com/download/temporary/fixture+%2B+package.pkg?expiration=123";
                var store = new SettingsStore(Path.Combine(root, "settings"));
                store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
                using var model = new MainViewModel(store);
                var artifact = new CatalogArtifact { Id = "release", Url = page, FileName = "fixture + package.pkg", Provider = "AkiraBox" };
                var entry = new CatalogEntry { Id = "fixture", SourceId = "fixture", Title = "AkiraBox handoff probe", Artifacts = [artifact] };
                var confirmations = 0; var pageClicks = false;
                window = new(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads, Path.Combine(root, "profile"), message =>
                {
                    if (message.StartsWith("Accept this download")) confirmations++;
                    return false; // Exact matches should never need this confirmation callback.
                }, diagnostics: true);
                    window.Browser.CoreWebView2InitializationCompleted += (_, e) =>
                    {
                        if (!e.IsSuccess) return;
                        var core = window.Browser.CoreWebView2;
                        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                        core.WebResourceRequested += (_, request) =>
                        {
                            var url = request.Request.Uri;
                            var content = url == page ? "<a id='download' aria-disabled='true' href='#' onclick=\"window.open('https://ads.invalid/');return false\">Confirm below</a><button id='verify' onclick=\"const a=document.getElementById('download');a.href='" + ready + "';a.setAttribute('aria-disabled','false');a.textContent='Download'\">Fixture verification</button>" :
                                unavailable ? "The link is not available at this time" : "synthetic package bytes";
                            pageClicks |= url.Contains("ads.invalid");
                            var headers = url == ready && !unavailable ? "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"fixture + package.pkg\"\r\n" : "Content-Type: text/html\r\n";
                            request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 200, "OK", headers);
                        };
                    };
                window.Show();
                await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
                Check(window.WorkflowGuidance.Text.Contains("Please wait") && window.WorkflowGuidance.Text.Contains("without clicking"), "persistent AkiraBox instructions visible");
                {
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('verify')") == "true");
                    await Task.Delay(700);
                    Check(confirmations == 0 && !window.Diagnostics.Snapshot().Any(e => e.Event == "ProviderReadyLink"), "disabled link cannot start handoff");
                    await window.Browser.ExecuteScriptAsync("document.getElementById('verify').click()");
                }
                await Wait(() => Task.FromResult(unavailable ? window.PageStatus.Text.Contains("reports that this link is unavailable") : model.Downloads.Count == 1 && !model.BrowserDownloads.HasActive));
                Check(confirmations == 0 && !pageClicks, "exact match automatically accepted without ad click handlers");
                Check(unavailable ? model.Downloads.Count == 0 && window.PageStatus.Text.Contains("No transfer was started") : model.Downloads.Single().State == "Downloaded" && File.ReadAllText(model.Downloads.Single().Destination) == "synthetic package bytes", "matching offer downloaded or provider error reported accurately");
                Console.WriteLine("PASS AkiraBox " + (unavailable ? "unavailable-link status; no transfer" : "automatic matching-file acceptance and verified synthetic bytes"));
                window.Close(); window = null;
            }
        }
        catch
        {
            if (window is not null)
            {
                Console.WriteLine("Provider status: " + window.PageStatus.Text);
                Console.WriteLine("Sanitised flow: " + window.Diagnostics.Export());
                Console.WriteLine("Ready anchor state: " + await window.Browser.ExecuteScriptAsync("(()=>{const a=document.querySelector('a#download');return {present:!!a,disabled:a?.getAttribute('aria-disabled'),visible:!!a?.getClientRects().length,readyRoute:!!a&&new URL(a.href).pathname.startsWith('/download/'),verificationFrame:!!document.querySelector('iframe[src*=turnstile],iframe[src*=recaptcha]')}})()"));
            }
            throw;
        }
        finally
        {
            window?.Close();
            for (var attempt = 0; attempt < 12 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { await Task.Delay(250); }
                catch (UnauthorizedAccessException) { await Task.Delay(250); }
            }
            if (Directory.Exists(root)) Console.WriteLine("Probe requires post-process cleanup: " + root);
        }
    }
    private static async Task Wait(Func<Task<bool>> condition, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("AkiraBox handoff probe timed out."); await Task.Delay(100); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
