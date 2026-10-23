using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

// Opt-in live acceptance: reads configured feeds/keys, but never owns or saves the user's settings.
internal static class OrbitFrontendLiveReadTests
{
    public static async Task RunAsync()
    {
        var originalStore = new SettingsStore();
        var originalBytes = File.ReadAllBytes(originalStore.FilePath);
        var configured = JsonSerializer.Deserialize<AppSettings>(originalBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var sources = configured.Sources.Where(source => source.Enabled).ToList();
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitFrontendLive-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root); store.Save(new() { Sources = sources, DownloadRoot = Path.Combine(root, "unused") });
        Window? window = null; OrbitFrontendHost? host = null;
        var evidence = Environment.GetEnvironmentVariable("PS5_ORBIT_LIVE_EVIDENCE");
        try
        {
            using var model = new MainViewModel(store);
            using var transport = new HttpTransfer();
            foreach (var source in model.Sources)
            {
                List<CatalogEntry> entries;
                try
                {
                    entries = source.SourceType switch
                    {
                        CatalogSourceType.OrbitLive => (await LiveOrbitCatalog.LoadAsync(source, transport)).Entries,
                        CatalogSourceType.OrbitLocal => (await LocalOrbitCatalog.LoadAsync(source)).Entries,
                        _ => (await transport.LoadCatalogAsync(source, CancellationToken.None)).Entries
                    };
                }
                catch (Exception ex) { Console.WriteLine("LIVE source unavailable: " + source.Name + " (" + ex.GetType().Name + ")"); continue; }
                foreach (var entry in entries) model.Entries.Add(entry);
                Console.WriteLine($"LIVE {source.Name}: {entries.Count} entries, {entries.Sum(entry => entry.Artifacts.Count)} releases");
            }
            var projectionClock = System.Diagnostics.Stopwatch.StartNew();
            using var perfApi = new OrbitFrontendApi(model);
            var snapshot = perfApi.Catalog;
            Console.WriteLine($"PERF catalogue projection: {projectionClock.ElapsedMilliseconds} ms");
            var jsonClock = System.Diagnostics.Stopwatch.StartNew();
            var catalogBytes = perfApi.SerializeResponse(snapshot.Releases);
            Console.WriteLine($"PERF catalogue JSON: {jsonClock.ElapsedMilliseconds} ms, {catalogBytes.Length:N0} bytes");
            jsonClock.Restart();
            if (!ReferenceEquals(catalogBytes, perfApi.SerializeResponse(snapshot.Releases))) throw new Exception("Unchanged catalogue response was rebuilt");
            Console.WriteLine($"PERF repeated catalogue JSON: {jsonClock.Elapsed.TotalMilliseconds:F3} ms");
            if (snapshot.Releases.Count == 0) throw new IOException("No configured live catalogue could be read.");
            host = new(); window = new() { Content = host, Width = 1300, Height = 850, ShowActivated = false };
            var readyClock = System.Diagnostics.Stopwatch.StartNew();
            window.Show(); await host.InitializeAsync(model);
            Console.WriteLine($"PERF WebView ready: {readyClock.ElapsedMilliseconds} ms");
            if (!string.IsNullOrWhiteSpace(evidence)) Directory.CreateDirectory(evidence);
            foreach (var (theme, width, height) in new[] { ("store", 1920, 1080), ("crossbar", 1280, 720), ("cinema", 1260, 800), ("panorama", 1920, 1080) })
            {
                host.Width = width; host.Height = height; window.Width = width + 32; window.Height = height + 64;
                await host.Browser.ExecuteScriptAsync($"localStorage.setItem('orbit.interface-notice.v1','dismissed');localStorage.setItem('orbit.interface.v1',JSON.stringify({{name:'{theme}',motion:'reduced',accent:'blue',ribbons:'dusk',artBehind:true}}));location.reload()");
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (await host.Browser.ExecuteScriptAsync($"!!document.querySelector('.iface-{theme}') && (document.querySelectorAll('[data-game]').length > 2 || document.body.innerText.includes('All games'))") != "true")
                { if (DateTime.UtcNow > deadline) throw new TimeoutException("Live " + theme + " did not render"); await Task.Delay(100); }
                if (theme == "crossbar") await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='Browse')?.click()");
                await Task.Delay(1800);
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    using var output = File.Create(Path.Combine(evidence, $"live-{theme}-{width}x{height}.png"));
                    await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                }
                var artwork = await host.Browser.ExecuteScriptAsync("[...document.images].filter(i=>i.complete&&i.naturalWidth>0&&i.currentSrc.startsWith('http')&&!i.currentSrc.includes('orbit.ps5catalog.invalid')).length");
                Console.WriteLine($"LIVE rendered {theme} {width}x{height}; loaded remote artwork: {artwork}");
            }
            if (model.Downloads.Count != 0 || model.Library.Count != 0 || !File.ReadAllBytes(originalStore.FilePath).SequenceEqual(originalBytes))
                throw new Exception("Read-only live acceptance changed application state");
            Console.WriteLine($"PASS live catalogue to WebView2: {snapshot.Releases.Count} releases; original settings/downloads/library byte-identical");
        }
        finally
        {
            host?.Dispose(); window?.Close();
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            { try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
        }
    }
}
