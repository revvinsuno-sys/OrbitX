using System.IO;
using System.Text.Json;
using System.Windows;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class CompatibilityTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5Compatibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root);
        var source = new CatalogSource { Id = "compat", Name = "Compatibility fixture" };
        store.Save(new() { Sources = [source], DownloadRoot = Path.Combine(root, "downloads") });
        var model = new MainViewModel(store);
        foreach (var (id, title) in new[] { ("PPSA03647", "Reported game"), ("PPSA99999", "Unmatched game") })
            model.Entries.Add(new() { SourceId = source.Id, Id = id, TitleId = id, Title = title, Platform = "PS5", Artifacts = [new() { Id = "release", TitleId = id }] });
        var host = new OrbitFrontendHost();
        var window = new Window { Content = host, Width = 1200, Height = 800, ShowActivated = false };
        try
        {
            window.Show(); await host.InitializeAsync(model);
            await Wait("document.querySelectorAll('[data-compatibility=unavailable]').length > 0");
            await model.RefreshCompatibilityAsync(_ => Task.FromResult(new Dictionary<string, string> { ["PPSA03647"] = "in-game" }));
            await Wait("document.querySelectorAll('[data-compatibility=\"in-game\"]').length > 0 && document.querySelectorAll('[data-compatibility=untested]').length > 0");
            host.NavigatePage("Browse");
            await Wait("[...document.querySelectorAll('[role=combobox]')].some(e=>document.getElementById(e.getAttribute('aria-labelledby'))?.textContent==='Compatibility')");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('[role=combobox]')].find(e=>document.getElementById(e.getAttribute('aria-labelledby'))?.textContent==='Compatibility').click()");
            await Wait("!!document.querySelector('[role=listbox]')");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('[role=option]')].find(e=>e.textContent==='In game').click()");
            await Wait("document.querySelectorAll('.browse-grid .game-tile').length===1 && document.querySelector('.browse-grid .game-tile').textContent.includes('Reported game')");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(e=>e.textContent==='Reset filters').click()");
            await Wait("document.querySelectorAll('.browse-grid .game-tile').length===2");
            await model.RefreshCompatibilityAsync(_ => throw new System.Net.Http.HttpRequestException("fixture offline"));
            await Wait("!!document.querySelector('[data-compatibility=\"in-game\"]')");
            foreach (var theme in new[] { "cinema", "panorama", "crossbar", "store" })
            {
                await host.Browser.ExecuteScriptAsync("localStorage.setItem('orbit.interface.v1',JSON.stringify({name:" + JsonSerializer.Serialize(theme) + ",motion:'reduced'}));location.reload()");
                await Wait("!!document.querySelector('.app.iface-" + theme + "')");
                if (theme != "store")
                    await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(e=>e.textContent.trim()==='Browse')?.click()");
                await Wait("!!document.querySelector('[data-compatibility=\"in-game\"]')");
                if (await host.Browser.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth") != "true")
                    throw new Exception("Compatibility badge overflows viewport: " + theme);
            }
        }
        finally
        {
            window.Close(); host.Dispose(); model.Dispose();
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
            if (Directory.Exists(root)) throw new IOException("Compatibility fixture remains in use: " + root);
        }
        Console.WriteLine("PASS compatibility: async catalogue invalidation, four-layout badges, dropdown filtering/reset, failed refresh preservation and fixture cleanup");
        async Task Wait(string expression)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (await host.Browser.ExecuteScriptAsync(expression) == "true") return;
                await Task.Delay(100);
            }
            throw new Exception("Compatibility UI condition timed out: " + expression + " " + await host.Browser.ExecuteScriptAsync("document.body.innerText.slice(0,1500)"));
        }
    }
}
