using System.IO;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class ProviderFlowTests
{
    public static async Task RunAsync()
    {
        var diagnostic = new ProviderFlowDiagnostics("private-selection");
        diagnostic.Record("Disabled", "https://example.test/private?token=secret");
        Check(diagnostic.Snapshot().Length == 0, "diagnostics default off");
        diagnostic.Enabled = true;
        for (var i = 0; i < 300; i++) diagnostic.Record("NavigationStarting", "https://example.test/private?token=secret#credential");
        diagnostic.Record("PopupDecision", "https://user:password@example.test/private", decision: "blocked");
        var export = diagnostic.Export();
        Check(diagnostic.Snapshot().Length == 256 && diagnostic.Snapshot()[0].Sequence == 46, "bounded ordered buffer");
        Check(!new[] { "private", "secret", "password", "credential", "token=" }.Any(export.Contains), "export excludes URL secrets and raw selection");
        Check(diagnostic.Snapshot().Last().Route == "unsafe-or-unsupported", "credential URL rejected without retaining credentials");

        var root = Path.Combine(Path.GetTempPath(), "PS5ProviderFlow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var server = new BrowserFixtures();
        BrowserDownloadWindow? window = null;
        try
        {
            foreach (var mode in new[] { "passive", "no-signal", "decline", "partial" })
            {
                var store = Store(root, mode);
                using var model = new MainViewModel(store);
                var entry = Entry(server);
                if (mode == "decline") entry.Artifacts[0].FileName = "different.pkg";
                entry.Artifacts[0].BrowserUrl = server.Url("/verification?mode=" + (mode == "no-signal" ? "no-signal" : "passive") + "&target=" + (mode == "partial" ? "/partial" : "/redirect"));
                var confirmations = 0;
                window = new(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads, Path.Combine(root, "profile"), message =>
                {
                    if (!message.StartsWith("Accept this download")) return true;
                    confirmations++;
                    window!.NavigationCompleted(false, CoreWebView2WebErrorStatus.ConnectionAborted, server.Url("/redirect"));
                    Check(window.PageStatus.Text.Contains("awaiting your confirmation") && !window.PageStatus.Text.Contains("could not load"), "pending offer wins over navigation failure");
                    return mode != "decline";
                }, diagnostics: true);
                window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Wait(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('verify')") == "true");
                Check(model.Downloads.Count == 0, "page opening alone does not admit a transfer");
                window.NavigationCompleted(false, CoreWebView2WebErrorStatus.ConnectionAborted, server.Url("/redirect"));
                Check(window.PageStatus.Text.Contains("could not load"), "navigation-only failure remains visible");
                await window.Browser.ExecuteScriptAsync("document.getElementById('verify').click()");
                if (mode == "no-signal")
                {
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("document.body.dataset.verified") == "\"true\"");
                    await Task.Delay(600);
                    Check(confirmations == 0 && model.Downloads.Count == 0 && !window.Diagnostics.Snapshot().Any(e => e.Event == "DownloadStarting"), "verification without browser signal cannot acquire a file");
                }
                else if (mode == "decline")
                {
                    await Wait(() => Task.FromResult(confirmations == 1));
                    Check(model.Downloads.Count == 0 && !model.BrowserDownloads.HasActive, "declined passive offer creates no owner");
                }
                else
                {
                    await Wait(() => Task.FromResult(model.Downloads.Count == 1));
                    var job = model.Downloads.Single();
                    await Wait(() => Task.FromResult(mode == "partial" ? job.State == "Interrupted" : !model.BrowserDownloads.IsActive(job)));
                    window.NavigationCompleted(false, CoreWebView2WebErrorStatus.ConnectionAborted, server.Url("/redirect"));
                    Check(!window.PageStatus.Text.Contains("could not load") && window.PageStatus.Text.Contains(job.State), "navigation failure cannot misreport owned transfer state");
                    Check(confirmations == 0 && job.SelectionIdentity == DownloadAdmission.Capture(entry, entry.Artifacts[0]), "automatic matching offer retains frozen selected release identity");
                    if (mode == "partial")
                    {
                        Check(job.State == "Interrupted" && model.Library.Count == 0 && !File.Exists(job.Destination), "real transfer failure remains visible without promotion");
                        Check(model.BrowserDownloads.IsActive(job), "interrupted operation retains ownership until native termination is acknowledged");
                    }
                    else Check(job.State == "Downloaded" && store.Load().Library.Count == 1 && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes), "passive native acquisition verified and persisted");
                    Check(!File.ReadAllText(store.FilePath).Contains("temporary-secret"), "native capability never persisted");
                }
                Console.WriteLine("FLOW " + mode + ": " + string.Join(" > ", window.Diagnostics.Snapshot().Select(e => e.Event + ":" + e.State)));
                window.Close();
                Check(window.Diagnostics.Snapshot().Last().Event == (mode == "partial" ? "InterruptionRequested" : "WindowClosed"), "closure or retained native owner recorded");
                window = null;
            }

            // Fixture ground truth is known; production URLs alone cannot prove popup content.
            foreach (var target in new[] { "/popup-ad", "/landing", "/file", "/redirect", "/expired", "http://192.0.2.1/unsafe", "https://unexpected.invalid/file" })
            {
                var store = Store(root, "popup-" + Guid.NewGuid().ToString("N"));
                using var model = new MainViewModel(store);
                var entry = Entry(server);
                entry.Artifacts[0].BrowserUrl = server.Url("/verification?mode=popup&target=" + Uri.EscapeDataString(target));
                var confirmations = 0;
                window = new(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads, Path.Combine(root, "profile"), _ => { confirmations++; return true; }, diagnostics: true);
                window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Wait(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('verify')") == "true");
                await window.Browser.ExecuteScriptAsync("document.getElementById('verify').click()");
                await Wait(() => Task.FromResult(window.Diagnostics.Snapshot().Any(e => e.Event == "PopupDecision")));
                Check(window.Diagnostics.Snapshot().Last(e => e.Event == "PopupDecision").Decision.StartsWith("blocked") && confirmations == 0 && model.Downloads.Count == 0, "default popup block retains no native owner: " + target);
                Console.WriteLine("POPUP " + target + ": " + window.Diagnostics.Snapshot().Last(e => e.Event == "PopupDecision").Decision);
                window.Close(); window = null;
            }

            // Existing explicit current-window option: confirmed user navigation is still required.
            var navigationStore = Store(root, "confirmed-popup");
            using (var model = new MainViewModel(navigationStore))
            {
                var entry = Entry(server); entry.Artifacts[0].BrowserUrl = server.Url("/verification?mode=popup&target=/redirect");
                var confirmations = 0;
                window = new(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads, Path.Combine(root, "profile"), _ => { confirmations++; return true; }, diagnostics: true);
                window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                window.BlockPopups.IsChecked = false;
                await Wait(async () => await window.Browser.ExecuteScriptAsync("!!document.getElementById('verify')") == "true");
                await window.Browser.ExecuteScriptAsync("document.getElementById('verify').click()");
                await Wait(() => Task.FromResult(window.Diagnostics.Snapshot().Any(e => e.Event == "PopupDecision")));
                var decision = window.Diagnostics.Snapshot().Last(e => e.Event == "PopupDecision");
                if (decision.UserInitiated == true)
                {
                    await Wait(() => Task.FromResult(model.Downloads.Count == 1 && !model.BrowserDownloads.HasActive));
                    Check(confirmations == 1 && model.Downloads.Single().State == "Downloaded", "confirmed popup navigation and automatic matching-file admission retained");
                }
                else Check(confirmations == 0 && model.Downloads.Count == 0, "non-user popup cannot navigate even with block setting off");
                Console.WriteLine("POPUP explicit option: user=" + decision.UserInitiated + " decision=" + decision.Decision);
                window.Close(); window = null;
            }
            using (var model = new MainViewModel(Store(root, "automatic-popup")))
            {
                var entry = Entry(server); entry.Artifacts[0].BrowserUrl = server.Url("/verification?mode=automatic-popup");
                var confirmations = 0;
                window = new(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads, Path.Combine(root, "profile"), _ => { confirmations++; return true; }, diagnostics: true);
                window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                window.BlockPopups.IsChecked = false;
                await Wait(() => Task.FromResult(window.Diagnostics.Snapshot().Any(e => e.Event == "PopupDecision")));
                var decision = window.Diagnostics.Snapshot().Last(e => e.Event == "PopupDecision");
                Check(decision.UserInitiated == false && decision.Decision == "blocked-no-confirmed-user-navigation" && confirmations == 0 && model.Downloads.Count == 0, "automatic popup rejected with blocking option off");
                Console.WriteLine("POPUP automatic: user=False decision=" + decision.Decision);
                window.Close(); window = null;
            }
            Console.WriteLine("PASS provider flow: bounded diagnostics, passive/no-signal, pending/navigation races, real interruption, popup classification and persisted state");
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
            if (Directory.Exists(root)) Console.WriteLine("Provider fixture requires post-process cleanup: " + root);
        }
    }

    private static CatalogEntry Entry(BrowserFixtures server)
    {
        var artifact = new CatalogArtifact { Id = "release", Url = "https://vikingfile.com/f/synthetic", Provider = "Vikingfile", FileName = "fixture.pkg", Size = server.Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(server.Bytes)) };
        return new() { Id = "selected", SourceId = "local-fixture", CatalogFormat = "Orbit", Title = "Synthetic", Artifacts = [artifact] };
    }
    private static SettingsStore Store(string root, string name)
    {
        var store = new SettingsStore(Path.Combine(root, name, "settings"));
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, name, "downloads") }); return store;
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Provider fixture timed out."); await Task.Delay(50); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
