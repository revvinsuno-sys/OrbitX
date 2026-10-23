using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class OrbitFrontendTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task RunAsync()
    {
        var trusted = OrbitFrontendSecurity.Document;
        foreach (var invalid in new[] { "{}", "null", "{", "{\"version\":\"1\",\"command\":\"fullscreen\"}", "{\"version\":1,\"command\":\"exec\"}", "{\"version\":1,\"command\":\"fullscreen\",\"path\":\"C:/\"}", "{\"version\":1,\"command\":\"navigate\",\"page\":\"file:///C:/\"}", "{\"version\":1,\"command\":\"ready\",\"command\":\"ready\"}" })
            Check(!OrbitFrontendSecurity.TryCommand(trusted, trusted, invalid, out _, out _, out _), "invalid bridge schema rejected");
        const string valid = "{\"version\":1,\"command\":\"fullscreen\"}";
        foreach (var untrusted in new[] { "https://provider.invalid/index.html", "file:///C:/index.html", "https://orbit.ps5catalog.invalid.evil.test/index.html", "https://orbit.ps5catalog.invalid/frame.html", trusted + "?command=exec", trusted + "#frame" })
            Check(!OrbitFrontendSecurity.TryCommand(untrusted, trusted, valid, out _, out _, out _) && !OrbitFrontendSecurity.TryCommand(trusted, untrusted, valid, out _, out _, out _), "both sender and current document must be trusted");
        Check(!OrbitFrontendSecurity.TryCommand(trusted, trusted, "{\"version\":1,\"command\":\"openRelease\",\"releaseId\":\"file:///C:/secret\"}", out _, out _, out _), "release handoff rejects paths");
        await ColdStartAsync();
        await OrbitInstallationFlowTests.RunAsync();
        await ManagedActionsAsync();

        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitFrontend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = new CatalogSource { Id = "fixture", Name = "Synthetic Orbit", SourceType = CatalogSourceType.OrbitLocal,
            EncryptedFilePath = Path.Combine(root, "catalog.enc"), KeyFilePath = Path.Combine(root, "key.bin") };
        File.WriteAllBytes(source.EncryptedFilePath, LocalOrbitCatalogTests.Seal(LocalOrbitCatalogTests.Feed()));
        File.WriteAllBytes(source.KeyFilePath, LocalOrbitCatalogTests.Key);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [source], DownloadRoot = Path.Combine(root, "downloads") });
        MainWindow? main = new(store) { ShowActivated = false };
        var model = (MainViewModel)main.DataContext;
        var operation = new NativeOwnershipTests.ControlledOperation();
        try
        {
            main.Show();
            await Wait(() => Task.FromResult(model.Entries.Count == 2));
            Check(model.Interface == "Classic Windows" && ((TabControl)main.FindName("ClassicTabs")).IsVisible, "Classic is the initial default");
            var downloads = model.BrowserDownloads;
            var activeJob = downloads.Accept(new() { Id = "active", SourceId = source.Id, Title = "Active fixture" },
                new() { Url = "https://example.test/landing", Artifact = new() { Id = "active", Url = "https://example.test/file" } }, "active.pkg", operation);
            operation.Write(activeJob.StagingPath, 16);
            model.Interface = "Orbit Store";
            var surface = (ContentControl)main.FindName("OrbitSurface");
            await Wait(() => Task.FromResult(surface.Content is OrbitFrontendHost));
            var host = (OrbitFrontendHost)surface.Content;
            await host.InitializeAsync(model);
            await Wait(async () => (await host.Browser.ExecuteScriptAsync("document.body.innerText")).Contains("Synthetic Orbit game"));
            using var api = new OrbitFrontendApi(model);
            var original = api.Catalog.Releases.Select(release => release.Id).Order().ToArray();
            var encoded = api.SerializeResponse(api.Catalog.Releases);
            Check(ReferenceEquals(encoded, api.SerializeResponse(api.Catalog.Releases)) && !System.Text.Encoding.UTF8.GetString(encoded).Contains("windowsIdentity"), "encoded catalogue is cached and raw native identities stay in C#");
            Check(api.Respond(trusted, "POST", OrbitFrontendSecurity.Origin + "/api/v1/windows/action", JsonSerializer.Serialize(new { action = "download", id = "win-release-" + new string('0', 64) })).Status == 409, "unknown release rejected by native API");
            Check(surface.IsVisible && model.Downloads.Count == 1, "unknown release cannot navigate or start a transfer");
            Check(api.Catalog.Releases.Count == 3 && api.Catalog.Releases.Select(release => release.GameId).Distinct().Count() == 2, "existing encrypted catalogue service feeds real React cards");
            Check(api.Respond(trusted, "GET", OrbitFrontendSecurity.Origin + "/api/v1/catalog", origin: "https://evil.invalid").Status == 403 &&
                api.Respond(trusted, "POST", OrbitFrontendSecurity.Origin + "/api/v1/downloads", "{}").Status == 405 &&
                api.Respond(trusted, "GET", OrbitFrontendSecurity.Origin + "/api/v1/settings").Status == 404, "no remote API access, download mutation or settings export");
            await host.Browser.ExecuteScriptAsync("document.querySelector('.game-card, .game-tile, .card')?.click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelector('.screen-details .hub-primary')?.textContent.includes('View releases')") == "true");
            Check(await host.Browser.ExecuteScriptAsync("document.querySelector('.app').dataset.game") == JsonSerializer.Serialize(api.Catalog.Releases.First(release => release.Platform == "ps4").GameId), "clicked PS4 card opens its own details");
            await host.Browser.ExecuteScriptAsync("document.querySelector('.hub-primary').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[role=dialog] .sheet-primary:not(:disabled)')") == "true");
            Check(await host.Browser.ExecuteScriptAsync("document.querySelector('[role=dialog]').textContent.includes('2.0')") == "true", "Store options show selected release metadata");
            model.Search = "Hidden by an old Windows filter";
            await host.Browser.ExecuteScriptAsync("document.querySelector('[role=dialog] .sheet-primary').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[aria-label=\"Download release\"] [data-windows-action=import]')") == "true");
            Check(surface.IsVisible && !((TabControl)main.FindName("ClassicTabs")).IsVisible && model.Downloads.Count == 1, "release controls open inside Orbit without starting an implicit transfer");
            ((Button)main.FindName("OrbitAppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.orbit-settings .iface-card') && !document.querySelector('[role=dialog]')") == "true");
            await host.Browser.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!document.querySelector('[role=dialog]')") == "true");
            await host.Browser.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[aria-label=\"App settings\"]')") == "true");
            await host.Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"App settings\"]').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.orbit-settings')") == "true");
            Check(surface.IsVisible, "Store primary Settings stays in the graphical workspace");
            host.ShowAppearance();
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.iface-card')") == "true");
            await host.Browser.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'F11',bubbles:true}))");
            await Wait(() => Task.FromResult(main.WindowStyle == WindowStyle.None));
            await host.Browser.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            await Task.Delay(150); Check(main.WindowStyle == WindowStyle.None, "Escape navigates Settings without exiting fullscreen");
            await host.Browser.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'F11',bubbles:true}))");
            await Wait(() => Task.FromResult(main.WindowStyle != WindowStyle.None));
            host.ShowAppearance();
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.iface-card')") == "true");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('.iface-card')].find(b=>b.textContent.includes('Crossbar')).click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[role=alertdialog]')") == "true");
            await host.Browser.ExecuteScriptAsync("document.querySelector('[role=alertdialog] .primary').click()");
            Check((await host.Browser.ExecuteScriptAsync("localStorage.getItem('orbit.interface.v1')")).Contains("crossbar"), "original Appearance cards save confirmed selection");
            var evidence = Environment.GetEnvironmentVariable("PS5_ORBIT_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence)) Directory.CreateDirectory(evidence);
            foreach (var (theme, width, height) in new[] { ("store", 1920, 1080), ("crossbar", 1280, 720), ("cinema", 1260, 800), ("panorama", 1920, 1080) })
            {
                await host.Browser.ExecuteScriptAsync($"localStorage.setItem('orbit.interface.v1', JSON.stringify({{name:'{theme}',motion:'reduced',accent:'blue',ribbons:'dusk',artBehind:true}}));location.reload()");
                await Wait(async () => await host.Browser.ExecuteScriptAsync($"!!document.querySelector('.app.iface-{theme}')") == "true");
                if (theme != "store")
                    await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='Browse')?.click()");
                await Wait(async () => await host.Browser.ExecuteScriptAsync("document.body.innerText.includes('Synthetic Orbit game')") == "true");
                await CheckNavigation(host);
                await host.Browser.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'F2',bubbles:true,cancelable:true}))");
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.orbit-settings .iface-card')") == "true");
                await host.Browser.ExecuteScriptAsync("document.querySelector('.main-nav button').click()");
                main.WindowState = WindowState.Normal; main.Width = Math.Max(width + 40, main.MinWidth); main.Height = Math.Max(height + 170, main.MinHeight);
                host.Width = width; host.Height = height;
                await Wait(async () => await host.Browser.ExecuteScriptAsync($"innerWidth==={width} && innerHeight==={height}") == "true");
                await Task.Delay(300);
                Check(await host.Browser.ExecuteScriptAsync("document.documentElement.scrollWidth <= innerWidth") == "true", theme + " fits the viewport");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    using var output = File.Create(Path.Combine(evidence, $"{theme}-{width}x{height}.png"));
                    await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                }
                Console.WriteLine($"PASS rendered {theme} {width}x{height}");
                Check(downloads.IsActive(activeJob) && operation.CancelRequests == 0 && operation.Subscribed, "appearance changes retain native writer ownership");
                host.NavigatePage("Downloads");
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[data-windows-id=\"" + activeJob.Id + "\"] [data-windows-action=cancel]')") == "true");
                Check(await host.Browser.ExecuteScriptAsync("document.documentElement.scrollWidth <= innerWidth") == "true", theme + " native queue fits viewport");
                host.NavigatePage("Library");
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[aria-label=\"My Library\"] [data-windows-action=addFolder]')") == "true");
                Check(await host.Browser.ExecuteScriptAsync("document.documentElement.scrollWidth <= innerWidth") == "true", theme + " native library fits viewport");
            }
            host.ClearValue(FrameworkElement.WidthProperty); host.ClearValue(FrameworkElement.HeightProperty);
            main.Width = 1260; main.Height = 800;
            await host.Browser.ExecuteScriptAsync("window.chrome.webview.postMessage({version:1,command:'fullscreen'})");
            await Wait(() => Task.FromResult(main.WindowStyle == WindowStyle.None));
            var chrome = (Border)main.FindName("NativeChrome");
            var statusStrip = (Border)main.FindName("NativeStatus");
            var layout = (Grid)main.FindName("ApplicationLayout");
            Check(statusStrip.Visibility == Visibility.Collapsed && layout.Margin == new Thickness(0), "fullscreen contains Orbit without native footer or outer margins");
            main.UpdateFullscreenChrome(new Point(400, 400), true);
            Check(!chrome.IsEnabled && (double)chrome.GetAnimationBaseValue(FrameworkElement.HeightProperty) == 0, "toolbar hides while pointer is over Orbit");
            main.UpdateFullscreenChrome(new Point(20, 1), true);
            Check(chrome.IsEnabled && (double)chrome.GetAnimationBaseValue(FrameworkElement.HeightProperty) > 0, "top screen edge reveals sliding toolbar");
            main.UpdateFullscreenChrome(new Point(20, 1), false);
            Check(!chrome.IsEnabled, "inactive window cannot reveal toolbar");
            main.RevealFullscreenToolbar();
            var closeApp = (Button)main.FindName("CloseAppButton");
            Check(closeApp.IsVisible && closeApp.IsEnabled && closeApp.TranslatePoint(new Point(), main).X > ((ComboBox)main.FindName("InterfacePicker")).TranslatePoint(new Point(), main).X, "revealed fullscreen toolbar exposes right-side Close control");
            Check(chrome.IsEnabled, "keyboard toolbar command reveals controls");
            main.UpdateFullscreenChrome(new Point(400, 400), false);
            await host.Browser.ExecuteScriptAsync("const input=document.createElement('input');document.body.append(input);input.focus();input.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));");
            await Task.Delay(100); Check(main.WindowStyle == WindowStyle.None, "Escape in input preserves fullscreen");
            await host.Browser.ExecuteScriptAsync("document.querySelector('input:last-of-type')?.blur();document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'F11',bubbles:true}));");
            await Wait(() => Task.FromResult(main.WindowStyle != WindowStyle.None));
            Check(main.Width == 1260 && main.Height == 800, "fullscreen restores native window size");
            Check(statusStrip.Visibility == Visibility.Visible && layout.Margin == new Thickness(12) && double.IsNaN((double)chrome.GetAnimationBaseValue(FrameworkElement.HeightProperty)), "exiting fullscreen restores native chrome and automatic toolbar height");
            // Exercise the original React search input and source/platform comboboxes with DOM input/click events.
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='Browse')?.click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('input[aria-label=\"Search games or title ID\"]')") == "true");
            async Task Search(string value)
            {
                await host.Browser.ExecuteScriptAsync("{const input=document.querySelector('input[aria-label=\"Search games or title ID\"]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input," + JsonSerializer.Serialize(value) + ");input.dispatchEvent(new Event('input',{bubbles:true}));}");
            }
            await Search("CUSA00001");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelectorAll('.game-tile').length === 1") == "true");
            await Search("no-such-title");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelectorAll('.game-tile').length === 0") == "true");
            await Search("");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('[role=combobox]')].find(b=>document.getElementById(b.getAttribute('aria-labelledby'))?.textContent==='Platform').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[role=option]')") == "true");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('[role=option]')].find(b=>b.textContent.trim()==='PS5').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelectorAll('.game-tile').length === 1 && !document.querySelector('.tile-platform')") == "true");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('[role=combobox]')].find(b=>document.getElementById(b.getAttribute('aria-labelledby'))?.textContent==='Source').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelectorAll('[role=option]').length === 2") == "true");
            await host.Browser.ExecuteScriptAsync("document.querySelectorAll('[role=option]')[1].click();document.querySelector('.game-tile').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.screen-details')") == "true");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelector('.windows-save-to')?.textContent.includes(" + JsonSerializer.Serialize(model.DownloadRoot) + ")") == "true");
            Check(await host.Browser.ExecuteScriptAsync("document.querySelector('.pn-page').lastElementChild.matches('.windows-save-to') && !document.querySelector('[data-setting=storage]')") == "true", "Save to shows actual download path in final read-only column");
            Check(await host.Browser.ExecuteScriptAsync("(()=>{const p=document.querySelector('.pn-page');p.scrollLeft=0;const e=new WheelEvent('wheel',{deltaY:120,bubbles:true,cancelable:true});p.dispatchEvent(e);return e.defaultPrevented&&p.scrollLeft>0})()") == "true", "vertical wheel scrolls real horizontal details page");
            var managementEvidence = Environment.GetEnvironmentVariable("PS5_ORBIT_MANAGEMENT_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(managementEvidence))
            {
                Directory.CreateDirectory(managementEvidence);
                await host.Browser.ExecuteScriptAsync("document.querySelector('.windows-save-to').scrollIntoView({inline:'end',block:'nearest'})");
                await Task.Delay(150);
                using var output = File.Create(Path.Combine(managementEvidence, "save-to-path.png"));
                await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            }
            Check(api.Catalog.Releases.Count(release => release.GameId == api.Catalog.Releases.First(release => release.Platform == "ps5").GameId) == 2, "PS5 alternatives remain distinct after UI filters");
            var versions = "[...document.querySelectorAll('.pn-group')].find(g=>g.getAttribute('aria-label')==='Versions')";
            Check(await host.Browser.ExecuteScriptAsync(versions + ".querySelectorAll('button').length") == "2", "both release options render in the original details selector");
            await host.Browser.ExecuteScriptAsync(versions + ".querySelectorAll('button')[1].click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync(versions + ".querySelectorAll('button')[1].getAttribute('aria-pressed')") == "\"true\"");
            await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>['Favourite','Add to favourites'].includes((b.getAttribute('aria-label')||document.getElementById(b.getAttribute('aria-labelledby'))?.textContent||b.textContent).trim())).click()");
            await Wait(() => Task.FromResult(model.Settings.OrbitFavorites.Count == 1));
            await host.Browser.ExecuteScriptAsync("window.chrome.webview.postMessage({version:1,command:'navigate',page:'Settings'})");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.orbit-settings')") == "true");
            Check(surface.IsVisible && model.Interface == "Orbit Store" && main.OwnedWindows.Count == 0, "Settings stays in the graphical application without a separate window");
            host.NavigatePage("Browse");
            var gameId = api.Catalog.Releases.First().GameId;
            Check(api.Respond(trusted, "POST", OrbitFrontendSecurity.Origin + "/api/v1/favorites", JsonSerializer.Serialize(new { gameId, favorite = true })).Status == 200, "favourites use native settings writer");
            File.WriteAllBytes(source.EncryptedFilePath, LocalOrbitCatalogTests.Seal(LocalOrbitCatalogTests.Feed(reverse: true)));
            await model.RefreshAsync(model.Sources.Single());
            Check(original.SequenceEqual(api.Catalog.Releases.Select(release => release.Id).Order()), "refresh preserves exact release IDs");
            Check(!ReferenceEquals(encoded, api.SerializeResponse(api.Catalog.Releases)), "catalogue refresh invalidates cached JSON");
            File.Delete(source.EncryptedFilePath); await model.RefreshAsync(model.Sources.Single());
            Check(original.SequenceEqual(api.Catalog.Releases.Select(release => release.Id).Order()) && model.Sources.Single().LastError.Length > 0, "offline source retains the existing validated snapshot and native error");
            File.WriteAllBytes(source.EncryptedFilePath, LocalOrbitCatalogTests.Seal(LocalOrbitCatalogTests.Feed()));
            host.Browser.CoreWebView2.Navigate("https://provider.invalid/"); await Task.Delay(200);
            Check(host.Browser.CoreWebView2.Source == trusted, "remote navigation cannot replace privileged document");
            model.Interface = "Classic Windows"; model.Interface = "Orbit Store";
            Check(ReferenceEquals(downloads, model.BrowserDownloads) && downloads.IsActive(activeJob) && operation.CancelRequests == 0 && operation.Subscribed && model.Sources.Single().Id == source.Id, "switching preserves active writer ownership and configuration");
            operation.Terminate();
            Check(!downloads.IsActive(activeJob) && model.Downloads.Single().Id == activeJob.Id, "native termination alone releases writer ownership");
            ((Button)main.FindName("CloseAppButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!main.IsVisible, "top menu Close uses normal window shutdown"); main = null;
            var saved = store.Load();
            Check(saved.Interface == "Orbit Store" && saved.OrbitFavorites.Contains(gameId) && saved.WindowPlacement?.Width == 1260, "interface, favourites and window bounds persist");
            main = new(store) { ShowActivated = false }; main.Show();
            await Wait(() => Task.FromResult(((ContentControl)main.FindName("OrbitSurface")).Content is OrbitFrontendHost));
            var reopened = (OrbitFrontendHost)((ContentControl)main.FindName("OrbitSurface")).Content;
            await reopened.InitializeAsync((MainViewModel)main.DataContext);
            await Wait(async () => await reopened.Browser.ExecuteScriptAsync("!!document.querySelector('.iface-panorama')") == "true");
            await reopened.Browser.ExecuteScriptAsync("window.dispatchEvent(new ErrorEvent('error',{message:'Synthetic render failure'}))");
            await Wait(() => Task.FromResult(((TabControl)main.FindName("ClassicTabs")).IsVisible));
            Check(((MainViewModel)main.DataContext).Interface == "Orbit Store" && ((MainViewModel)main.DataContext).Status.Contains("rendering error"), "render failure exposes Classic without wiping the saved preference");
            ((MainViewModel)main.DataContext).Interface = "Classic Windows";
            Check(((TabControl)main.FindName("ClassicTabs")).IsVisible, "saved appearance reopens and Classic remains available");
            Console.WriteLine("PASS Orbit WebView2 bridge, encrypted catalogue, modes, fullscreen, settings and fallback");
        }
        finally { operation.Terminate(); main?.Close(); await Cleanup(root); }
    }
    internal static async Task ManagedActionsAsync()
    {
        using var server = new RangeFixtureServer(4 * 1024 * 1024, 10);
        using var browserServer = new BrowserFixtures();
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitManagement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [], Interface = "Orbit Store", DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = Path.Combine(root, "installed") });
        MainWindow? main = new(store) { ShowActivated = false }; var model = (MainViewModel)main.DataContext;
        var nativeWriter = new NativeOwnershipTests.ControlledOperation();
        try
        {
            main.Show();
            var surface = (ContentControl)main.FindName("OrbitSurface");
            await Wait(() => Task.FromResult(surface.Content is OrbitFrontendHost));
            var host = (OrbitFrontendHost)surface.Content; await host.InitializeAsync(model);
            var source = new CatalogSource { Id = "fixture", Name = "Management fixture" }; model.Sources.Add(source);
            var artifact = new CatalogArtifact { Id = "direct", Url = server.Url("good"), FileName = "fixture.pkg", Size = server.Bytes.Length, Sha256 = server.Digest };
            var entry = new CatalogEntry { Id = "management", SourceId = source.Id, Title = "Management fixture game", Artifacts = [artifact] }; model.Entries.Add(entry);
            using var api = new OrbitFrontendApi(model);
            var release = api.Catalog.Releases.Single();
            source.Enabled = false;
            Check(api.Respond(OrbitFrontendSecurity.Document, "POST", OrbitFrontendSecurity.Origin + "/api/v1/windows/action", JsonSerializer.Serialize(new { action = "download", id = release.Id })).Status == 409, "disabled source cannot use an already cached release identity");
            source.Enabled = true;
            foreach (var body in new[] { "{}", "null", "{", "{\"action\":\"download\",\"id\":\"x\",\"path\":\"C:/\"}", "{\"action\":\"settings\",\"id\":\"\",\"id\":\"\"}", "{\"action\":\"exec\",\"id\":\"\"}" })
                Check(api.Respond(OrbitFrontendSecurity.Document, "POST", OrbitFrontendSecurity.Origin + "/api/v1/windows/action", body).Status is 400 or 409, "invalid native action schema/arguments rejected");
            Check(api.Respond("https://provider.invalid/index.html", "POST", OrbitFrontendSecurity.Origin + "/api/v1/windows/action", "{\"action\":\"settings\",\"id\":\"\"}").Status == 403, "provider document cannot invoke native actions");
            await host.Browser.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('windows-release',{detail:" + JsonSerializer.Serialize(release.Id) + "}))");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[data-windows-action=download]')") == "true");
            await host.Browser.ExecuteScriptAsync("document.querySelector('[data-windows-action=download]').click()");
            await Wait(() => Task.FromResult(model.SelectedJob is { CanPause: true, DownloadedBytes: > 65536 }));
            var job = model.SelectedJob!;
            await Wait(async () => (await host.Browser.ExecuteScriptAsync("document.querySelector('.windows-toast')?.textContent || ''")).Contains("Download added:"));
            Check(await host.Browser.ExecuteScriptAsync("!document.querySelector('[aria-label=\"Downloads\"].windows-management')") == "true", "added download shows toast without changing screen");
            host.NavigatePage("Downloads");
            await Click("pause", job.Id);
            await Wait(() => Task.FromResult(job.State == "Paused" && !model.HasDirectTransfers));
            Check(File.Exists(job.StagingPath) && surface.IsVisible, "Orbit Pause joins the existing HTTP writer and keeps its partial");
            await Click("resume", job.Id);
            await Wait(() => Task.FromResult(job.State == "Downloaded" && !model.HasDirectTransfers));
            Check(job.ActualSha256 == server.Digest && File.ReadAllBytes(job.Destination).SequenceEqual(server.Bytes), "Orbit download and Resume use existing verified completion");
            await Click("clearDownload", job.Id);
            await Wait(() => Task.FromResult(!model.Downloads.Contains(job)));
            Check(File.Exists(job.Destination), "Orbit history clear preserves completed files");
            Console.WriteLine("PASS Orbit direct download, pause/resume and history preservation");

            var archive = Path.Combine(root, "synthetic.rar"); File.WriteAllBytes(archive, Convert.FromBase64String(PackageArchiveTests.Rar5));
            var rarArtifact = new CatalogArtifact { Id = "archive", Url = "https://fixture.invalid/synthetic.rar", FileName = "synthetic.rar", Size = new FileInfo(archive).Length };
            var rarEntry = new CatalogEntry { Id = "rar", Title = "Orbit RAR fixture", Artifacts = [rarArtifact] };
            await model.BrowserDownloads.ImportAsync(rarEntry, DownloadMirror.ForEntry(rarEntry).Single(), archive);
            var rarJob = model.Downloads.Single();
            await Click("installDownload", rarJob.Id);
            await Wait(() => Task.FromResult(!model.IsExtracting && !model.IsMovingContent && model.LibraryView.Cast<LocalLibraryEntry>().Any(item => item.Title == rarEntry.Title)));
            var installed = model.LibraryView.Cast<LocalLibraryEntry>().Single();
            Check(File.Exists(archive) && Directory.Exists(installed.ContentPath), "Orbit extraction invokes existing RAR installation and source retention");
            host.NavigatePage("Library");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[data-windows-id=\"" + installed.Id + "\"] .library-more-button')") == "true");
            await host.Browser.ExecuteScriptAsync("document.querySelector('[data-windows-id=\"" + installed.Id + "\"] .library-more-button').click()");
            await Click("reinspect", installed.Id);
            await Wait(() => Task.FromResult(model.SelectedLibraryEntry == installed && installed.Integrity == "Installed content SHA-256 verified"));
            Check(surface.IsVisible && model.SelectedLibraryEntry == installed, "Orbit My Library addresses the native installed entry");
            await Capture("library");
            foreach (var ps5 in new[] { false, true })
            {
                var package = Path.Combine(root, ps5 ? "ps5.pkg" : "ps4.pkg"); PackageArchiveTests.BuildPackage(package, ps5);
                var pkgArtifact = new CatalogArtifact { Id = ps5 ? "ps5" : "ps4", Url = "https://fixture.invalid/" + Path.GetFileName(package), FileName = Path.GetFileName(package), Size = new FileInfo(package).Length };
                var pkgEntry = new CatalogEntry { Id = pkgArtifact.Id, Title = "Orbit " + pkgArtifact.Id + " package", Artifacts = [pkgArtifact] };
                await model.BrowserDownloads.ImportAsync(pkgEntry, DownloadMirror.ForEntry(pkgEntry).Single(), package);
                var pkgJob = model.Downloads.Single(job => job.ArtifactId == pkgArtifact.Id);
                model.RetainOriginalImage = true;
                host.NavigatePage("Downloads"); await Click("installDownload", pkgJob.Id);
                await Wait(() => Task.FromResult(!model.IsInstalling && model.Installations.Any(job => job.SourcePath == pkgJob.Destination && job.Registered)));
                var installation = model.Installations.Single(job => job.SourcePath == pkgJob.Destination);
                Check(File.Exists(package) && File.Exists(pkgJob.Destination) && model.LibraryView.Cast<LocalLibraryEntry>().Any(item => item.InstalledGame?.InstallationId == installation.Id), "Orbit installs " + pkgArtifact.Id + " through existing confirmations and package readers");
            }
            Console.WriteLine("PASS Orbit PS4/PS5 PKG install controls, Settings retention and registered library results without confirmation");
            var installationPaths = model.Installations.Select(job => job.Destination).ToArray();
            await Click("clearInstallations");
            await Wait(() => Task.FromResult(model.Installations.All(job => job.IsCleared)));
            Check(installationPaths.All(Directory.Exists), "Orbit installation history clear preserves installed content");

            // Browser cancellation must retain ownership until the real native operation terminates.
            var nativeJob = model.BrowserDownloads.Accept(new() { Id = "native", Title = "Orbit browser writer" },
                new() { Url = "https://fixture.invalid/landing", Artifact = new() { Id = "native", Url = "https://fixture.invalid/file" } }, "native.pkg", nativeWriter);
            nativeWriter.Write(nativeJob.StagingPath, 32); host.NavigatePage("Downloads");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.body.innerText.includes('Orbit browser writer')") == "true");
            await Capture("downloads");
            await Click("cancel", nativeJob.Id);
            await Wait(() => Task.FromResult(nativeWriter.CancelRequests == 1));
            Check(model.BrowserDownloads.IsActive(nativeJob) && File.Exists(nativeJob.StagingPath), "Orbit cancellation retains native writer ownership and bytes");
            nativeWriter.Terminate();
            Check(!model.BrowserDownloads.IsActive(nativeJob), "native terminal event releases Orbit-requested cancellation");

            // The same release control must open the pre-existing provider window, with no privileged bridge.
            artifact.Url = browserServer.Url("/landing?target=/file"); artifact.Size = browserServer.Bytes.Length; artifact.Sha256 = "";
            await host.Browser.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('windows-release',{detail:" + JsonSerializer.Serialize(release.Id) + "}))");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[aria-label=\"Download release\"] [data-windows-action=browser]')") == "true");
            Check(await host.Browser.ExecuteScriptAsync("[...document.querySelectorAll('.windows-method button')].every(b=>{const p=document.getElementById(b.getAttribute('aria-describedby'));return p && p.textContent.trim().length>0 && p.getClientRects().length>0})") == "true", "each release method has visible accessible instructions");
            await Capture("release-instructions");
            await host.Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"Download release\"] [data-windows-action=browser]').click()");
            await Wait(() => Task.FromResult(main.OwnedWindows.OfType<BrowserDownloadWindow>().Any()));
            var provider = main.OwnedWindows.OfType<BrowserDownloadWindow>().Single();
            await Wait(() => Task.FromResult(provider.Browser.CoreWebView2 is not null));
            Check(!provider.Browser.CoreWebView2.Settings.IsWebMessageEnabled && surface.IsVisible, "Orbit browser action reuses isolated existing provider window");
            provider.Close();
            host.NavigatePage("Downloads");
            var scannedPath = Path.Combine(model.DownloadRoot, "scan-ui-fixture.zip"); File.WriteAllBytes(scannedPath, "synthetic scanned bytes"u8.ToArray());
            var scannedFolder = Path.Combine(model.DownloadRoot, "Scanned extracted folder"); Directory.CreateDirectory(scannedFolder);
            File.WriteAllText(Path.Combine(scannedFolder, "nested.pkg"), "synthetic extracted package");
            await Click("scanDownloads");
            await Wait(() => Task.FromResult(model.Downloads.Any(job => job.Destination == scannedPath)));
            var scannedJob = model.Downloads.Single(job => job.Destination == scannedPath);
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelector('[data-windows-id=\"" + scannedJob.Id + "\"] .windows-badge')?.textContent") == "\"Unmatched metadata\"");
            Check(await host.Browser.ExecuteScriptAsync("!!document.querySelector('[data-windows-action=scanDownloads]')") == "true", "Orbit Scan downloads reimports files with visible metadata status");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("document.querySelector('[aria-label=Downloads]').innerText.includes('Scan complete:') && document.querySelector('[aria-label=Downloads]').innerText.includes('Local file')") == "true");
            Check(model.LibraryView.Cast<LocalLibraryEntry>().Any(item => item.Title == "Scanned extracted folder") &&
                !model.Downloads.Any(job => Path.GetFileName(job.Destination) == "nested.pkg"), "Orbit scan registers one extracted folder in My Library without per-asset Downloads");
            await Capture("scan-downloads");
            await Click("settings");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.orbit-settings')") == "true");
            await host.Browser.ExecuteScriptAsync("document.querySelector('[data-settings-category=downloads]').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('[data-setting=DownloadRoot]')") == "true");
            Check(surface.IsVisible && await host.Browser.ExecuteScriptAsync("document.querySelector('[data-setting=DownloadBandwidthMBps]').value") == "\"100\"", "graphical Settings displays the shared bandwidth cap");
            await host.Browser.ExecuteScriptAsync("let bandwidth=document.querySelector('[data-setting=DownloadBandwidthMBps]');bandwidth.focus();Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(bandwidth,'80');bandwidth.dispatchEvent(new Event('input',{bubbles:true}));bandwidth.blur()");
            await Wait(() => Task.FromResult(store.Load().DownloadBandwidthMBps == 80));
            await host.Browser.ExecuteScriptAsync("document.querySelector('[data-settings-category=passwords]').click()");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.settings-passwords li')") == "true");
            Check(await host.Browser.ExecuteScriptAsync("!document.querySelector('.settings-passwords').textContent.includes('DLPSGAME.COM')") == "true", "graphical Settings masks saved default passwords");
            host.NavigatePage("Downloads");
            Check(!((TabControl)main.FindName("ClassicTabs")).IsVisible && model.Interface == "Orbit Store", "all management stays in Orbit");
            Console.WriteLine("PASS Orbit native direct/pause/resume/clear, RAR installation, library reinspection, browser workflow, cancellation ownership and Settings");

            async Task Click(string action, string id = "")
            {
                var selector = (id.Length > 0 ? ":is([data-windows-id='" + id + "'], [data-library-management='" + id + "']) " : "") + "[data-windows-action='" + action + "']";
                await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector(" + JsonSerializer.Serialize(selector) + ")") == "true");
                await host.Browser.ExecuteScriptAsync("document.querySelector(" + JsonSerializer.Serialize(selector) + ").click()");
            }
            async Task Capture(string page)
            {
                var evidence = Environment.GetEnvironmentVariable("PS5_ORBIT_MANAGEMENT_EVIDENCE");
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence); await Task.Delay(150);
                using var output = File.Create(Path.Combine(evidence, page + ".png"));
                await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            }
        }
        finally { nativeWriter.Terminate(); await model.StopDirectDownloadsAsync(); main.Close(); await Cleanup(root); }
    }
    private static async Task CheckNavigation(OrbitFrontendHost host)
    {
        await host.Browser.ExecuteScriptAsync("""
            window.navigationCheck = null;
            void (async () => {
              const frame = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
              let moved = false;
              for (const [arrow, letter] of [['ArrowUp','w'],['ArrowLeft','a'],['ArrowDown','s'],['ArrowRight','d']]) {
                const start = document.querySelector('.game-tile, .xb-row-main, .cn-frame, .pn-tile-hit');
                if (!start) { window.navigationCheck = 'No game control'; return; }
                start.focus(); await frame();
                start.dispatchEvent(new KeyboardEvent('keydown', {key:arrow,bubbles:true,cancelable:true})); await frame();
                const expected = document.activeElement;
                moved ||= expected !== start;
                start.focus(); await frame();
                start.dispatchEvent(new KeyboardEvent('keydown', {key:letter,bubbles:true,cancelable:true})); await frame();
                if (document.activeElement !== expected) { window.navigationCheck = 'Mismatch '+letter; return; }
              }
              const input = document.createElement('input'); document.body.append(input); input.focus();
              const typing = new KeyboardEvent('keydown',{key:'w',bubbles:true,cancelable:true}); input.dispatchEvent(typing);
              const preserved = !typing.defaultPrevented && document.activeElement === input;
              input.remove(); window.navigationCheck = moved && preserved ? 'pass' : 'No movement or typing intercepted';
            })();
            """);
        await Wait(async () => await host.Browser.ExecuteScriptAsync("window.navigationCheck !== null") == "true");
        var result = await host.Browser.ExecuteScriptAsync("window.navigationCheck");
        Check(result == "\"pass\"", "arrow keys and WASD move equally without capturing typing: " + result);
    }
    private static async Task ColdStartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitColdStart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var key = Path.Combine(root, "key.bin"); File.WriteAllBytes(key, LocalOrbitCatalogTests.Key);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Interface = "Orbit Store", DownloadRoot = Path.Combine(root, "downloads"), Sources = [new() { Name = "Pending catalogue", SourceType = CatalogSourceType.OrbitLive, Url = LiveOrbitCatalog.OfficialUrl, KeyFilePath = key }] });
        using var transport = new HttpTransfer(new PendingFeed());
        var window = new MainWindow(store, orbitTransfer: transport) { ShowActivated = false };
        try
        {
            window.Show(); var surface = (ContentControl)window.FindName("OrbitSurface");
            await Wait(() => Task.FromResult(surface.Content is OrbitFrontendHost));
            await ((OrbitFrontendHost)surface.Content).InitializeAsync((MainViewModel)window.DataContext);
            Check(((MainViewModel)window.DataContext).Entries.Count == 0 && surface.IsVisible, "Orbit opens while a catalogue request is still pending");
        }
        finally { window.Close(); await Cleanup(root); }
    }
    private sealed class PendingFeed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
    }
    private static async Task Cleanup(string root)
    {
        // WebView2 owns profile files until its subprocess exits; only delete after it releases them.
        for (var attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
    private static async Task Wait(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline) { if (await predicate()) return; await Task.Delay(100); }
        throw new TimeoutException("Orbit UI condition timed out.");
    }
    private static void Check(bool value, string description) { if (!value) throw new Exception(description); }
}
