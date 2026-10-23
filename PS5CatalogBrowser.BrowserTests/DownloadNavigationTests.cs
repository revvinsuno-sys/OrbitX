using System.IO;
using System.Text.Json;
using System.Windows;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class DownloadNavigationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5DownloadNavigation-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [new() { Id = "fixture", Name = "Synthetic navigation" }],
            DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = Path.Combine(root, "installed") });
        var model = new MainViewModel(store);
        for (var index = 0; index < 18; index++)
        {
            var entry = new CatalogEntry { Id = "game-" + index, SourceId = "fixture", Title = "Navigation game " + index, Platform = "PS5",
                Artifacts = [new() { Id = "file-" + index, FileName = index + ".pkg", Size = 100, Url = "https://fixture.invalid/" + index }] };
            model.Entries.Add(entry);
            model.Downloads.Add(new() { EntryId = entry.Id, SourceId = entry.SourceId, ArtifactId = entry.Artifacts[0].Id,
                Title = entry.Title, State = "Paused", FileName = index + ".pkg", TotalBytes = 100, Destination = Path.Combine(model.DownloadRoot, index + ".pkg") });
        }
        using var api = new OrbitFrontendApi(model);
        using var host = new OrbitFrontendHost();
        var window = new Window { Content = host, Width = 1100, Height = 700 };
        try
        {
            window.Show(); await host.InitializeAsync(model);
            var target = model.Downloads.Last();
            var gameId = api.Catalog.Releases.Single(release => release.WindowsIdentity.EntryId == target.EntryId).GameId;
            foreach (var theme in new[] { "store", "cinema", "crossbar", "panorama" })
            {
                await Script("localStorage.setItem('orbit.interface.v1',JSON.stringify({name:" + JsonSerializer.Serialize(theme) + ",motion:'reduced'}));location.reload()");
                await Wait("!!document.querySelector('#root .app')");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await Script("window.dispatchEvent(new CustomEvent('windows-game',{detail:{id:" + JsonSerializer.Serialize(gameId) + ",query:''}}))");
                    const string button = "[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='View download'||b.getAttribute('aria-label')==='View download')";
                    await Wait("!!" + button);
                    await Script(button + ".click()");
                    await Wait("!!document.querySelector('section[aria-label=Downloads]')");
                    var row = "document.querySelector('[data-windows-id=\"" + target.Id + "\"]')";
                    await Wait("(()=>{const r=" + row + ";if(!r)return false;const b=r.getBoundingClientRect();return document.activeElement===r&&b.top>=0&&b.bottom<=innerHeight})()");
                    // Polling updates must leave a user's subsequent scrolling and focus alone.
                    await Script("document.querySelector('.windows-management h1').scrollIntoView();document.querySelector('.windows-management button').focus({preventScroll:true})");
                    await Task.Delay(1300);
                    if (await Script("document.activeElement===" + row) == "true") throw new Exception("Download polling stole focus");
                }
            }
            if (model.Downloads.Count != 18 || model.Downloads.Any(job => job.State != "Paused"))
                throw new Exception("Navigation changed the download queue");
            Console.WriteLine("PASS View download in Store, Cinema, Crossbar and Panorama navigates, scrolls and focuses the matching offscreen game on repeated visits; polling preserves user focus");
        }
        finally
        {
            window.Close(); host.Dispose(); model.Dispose();
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            { try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
        }

        Task<string> Script(string code) => host.Browser.ExecuteScriptAsync(code);
        async Task Wait(string code)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline) { if (await Script(code) == "true") return; await Task.Delay(60); }
            throw new Exception("Download navigation timeout: " + code + " " + await Script("document.body.innerText.slice(-1500)"));
        }
    }
}
