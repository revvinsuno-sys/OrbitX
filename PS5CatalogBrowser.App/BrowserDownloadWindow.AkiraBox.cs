using System.Text.Json;
using System.Windows.Threading;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public partial class BrowserDownloadWindow
{
    private readonly DispatcherTimer akiraTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool akiraReading, akiraAttempted, providerClosed;
    private int akiraGeneration;
    private DateTimeOffset akiraStarted;
    private void ConfigureAkiraBox()
    {
        if (AkiraBoxLink.FileId(mirror.BrowserUrl) is null) return;
        WorkflowGuidance.Text = AkiraBoxLink.Instructions + " Keep this window open while transferring. " +
            "If AkiraBox reports the link unavailable, Reload returns to the original file page for a fresh link. File size and checksum checks still apply.";
        var core = Browser.CoreWebView2;
        core.NavigationStarting += (_, e) =>
        {
            akiraGeneration++; akiraTimer.Stop();
            if (AkiraBoxLink.FileId(e.Uri) == AkiraBoxLink.FileId(mirror.BrowserUrl))
            { akiraAttempted = false; akiraStarted = DateTimeOffset.UtcNow; akiraTimer.Start(); }
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (e.IsSuccess && akiraAttempted && accepted.Count == 0 && pendingOffers == 0) await ReportAkiraErrorAsync();
        };
        akiraTimer.Tick += async (_, _) => await InspectAkiraAsync();
        Closed += (_, _) => { providerClosed = true; akiraTimer.Stop(); };
    }
    private async Task InspectAkiraAsync()
    {
        if (akiraReading || akiraAttempted || providerClosed || pendingOffers > 0 || accepted.Count > 0) return;
        if (DateTimeOffset.UtcNow - akiraStarted > TimeSpan.FromMinutes(2))
        { akiraTimer.Stop(); PageStatus.Text = "AkiraBox has not offered a ready link. Complete any verification, then Reload to try a fresh file page, or use external browser/import."; return; }
        akiraReading = true; var generation = akiraGeneration;
        try
        {
            var json = await Browser.ExecuteScriptAsync("""
                (() => {
                  const link = document.querySelector('a#download');
                  if (!link || link.getAttribute('aria-disabled') === 'true' || !link.getClientRects().length || link.href.length > 16384) return null;
                  return { page: location.href, href: link.href };
                })()
                """);
            if (providerClosed || generation != akiraGeneration || json.Length > 32768 || json == "null") return;
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 3 });
            var page = document.RootElement.GetProperty("page").GetString()!;
            var href = document.RootElement.GetProperty("href").GetString()!;
            if (Browser.CoreWebView2.Source != page || !AkiraBoxLink.Ready(mirror.BrowserUrl, page, href, mirror.ExpectedFileName)) return;
            akiraAttempted = true; akiraTimer.Stop();
            Diagnostics.Record("ProviderReadyLink", href, decision: "native-browser-navigation");
            PageStatus.Text = "AkiraBox link ready. Waiting for its native file offer; size and checksum checks still apply.";
            // Navigate from the provider document: normal browser cookies/referrer remain inside
            // this WebView. Do not replay the capability through HTTP, forward cookies or click ad handlers.
            await Browser.ExecuteScriptAsync("if(location.href===" + JsonSerializer.Serialize(page) + "&&document.querySelector('a#download')?.href===" + JsonSerializer.Serialize(href) + ")location.assign(" + JsonSerializer.Serialize(href) + ")");
        }
        catch (Exception)
        { akiraTimer.Stop(); if (!providerClosed) PageStatus.Text = "Could not inspect AkiraBox's ready link. Reload the file page or use external browser/import."; }
        finally { akiraReading = false; }
    }
    private async Task ReportAkiraErrorAsync()
    {
        var generation = akiraGeneration;
        try
        {
            var unavailable = await Browser.ExecuteScriptAsync("document.body?.innerText.includes('The link is not available at this time')===true");
            if (!providerClosed && generation == akiraGeneration && accepted.Count == 0 && pendingOffers == 0 && unavailable == "true")
            {
                PageStatus.Text = "AkiraBox reports that this link is unavailable. No transfer was started. Reload returns to the file page for a fresh link; if it repeats, try another release or external browser/import.";
                RequestFreshRelease();
            }
        }
        catch (Exception) { /* A download/navigation may replace the document during inspection. */ }
    }
}
