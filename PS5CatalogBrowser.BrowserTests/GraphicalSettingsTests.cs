using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class GraphicalSettingsTests
{
    internal static async Task RunAsync(MainWindow window, MainViewModel model, SettingsStore store, string root)
    {
        model.Interface = "Orbit Store";
        var surface = (ContentControl)window.FindName("OrbitSurface");
        await Wait(() => surface.Content is OrbitFrontendHost);
        var host = (OrbitFrontendHost)surface.Content!;
        await host.InitializeAsync(model); await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        host.Browser.Focus();
        var categories = new[] { "sources", "downloads", "passwords", "browser", "installation", "emulators", "appearance", "language", "metadata", "about" };
        foreach (var theme in new[] { "cinema", "panorama", "crossbar", "store" })
        {
            await Reload($"localStorage.setItem('orbit.interface.v1',JSON.stringify({{name:'{theme}',motion:'reduced'}}));location.reload()");
            await Until($"!!document.querySelector('.app.iface-{theme}')");
            await Until("!document.querySelector('.orbitx-startup')");
            await Nav("Browse");
            await Script("window.settingsHeader=document.querySelector('.main-nav')");
            await Nav("Settings");
            await Until("document.querySelectorAll('[data-settings-category]').length===10");
            Check(surface.IsVisible && model.Interface == "Orbit Store" && !((TabControl)window.FindName("ClassicTabs")).IsVisible,
                "graphical Settings never leaves WebView or enters Classic: " + theme);
            await Assert("document.querySelector('.main-nav')===window.settingsHeader && !!document.querySelector('.main-nav [aria-current=page]') && !document.querySelector('[role=dialog]')", "same graphical header and page ownership: " + theme);
            await Assert("[...document.querySelectorAll('[data-settings-category]')].every(tile=>getComputedStyle(tile).borderTopWidth==='0px')", "borderless category tiles: " + theme);
            await CheckTileRail();
            await Capture(theme + "-home.png");
            await Choose("downloads"); await Key("Escape"); await Until("!!document.querySelector('.settings-home')");
            await Key("Escape"); await Until("!document.querySelector('.orbit-settings')");
            await Until("document.activeElement===document.querySelector('.main-nav [aria-current=page]')");
            await Assert("document.querySelector('.main-nav [aria-current=page]').textContent.trim()==='Browse'", "Escape returns to the actual previous page: " + theme);
            await Nav("Settings"); await Choose("emulators");
            await Script("document.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
            await Until("!!document.querySelector('.settings-home')");
            await Script("document.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
            await Until("!document.querySelector('.orbit-settings')");
        }
        // Exercise the existing controller polling loop, including its neutral-input focus fence.
        await Script("window.testPad={buttons:Array.from({length:17},()=>({pressed:false,value:0})),axes:[0,0,0,0]};Object.defineProperty(navigator,'getGamepads',{configurable:true,value:()=>[window.testPad]})");
        await Nav("Settings"); await Choose("emulators"); await Task.Delay(120);
        await PadBack(); await Until("!!document.querySelector('.settings-home')");
        await PadBack(); await Until("!document.querySelector('.orbit-settings')");
        await Assert("document.querySelector('.main-nav [aria-current=page]').textContent.trim()==='Browse'", "controller B returns through category, hub and previous page");

        await Nav("Settings"); await Choose("sources");
        await Script("document.querySelector('.settings-source-list > button').click()");
        await SetInput("[data-source=name]", "Graphical source draft");
        await Choose("downloads"); await Choose("sources");
        await Assert("document.querySelector('[data-source=name]').value==='Graphical source draft'", "source drafts survive category navigation");
        await Script("document.querySelector('[data-settings-action=sourceSave]').click()");
        await Wait(() => store.Load().Sources.Single().Name == "Graphical source draft");
        Check(!store.Load().Sources.Single().Enabled && model.Sources.Single().Url.StartsWith("https://example.invalid/"), "source Save uses the existing handler and retains disabled connection values");
        var sourceEditor = (FrameworkElement)window.FindName("SourceEditorPanel");
        sourceEditor.IsEnabled = false;
        await Until("document.querySelector('.settings-source-list button').disabled && document.querySelector('.orbit-settings-content fieldset').disabled");
        sourceEditor.IsEnabled = true;
        await Until("!document.querySelector('.settings-source-list button').disabled && !document.querySelector('.orbit-settings-content fieldset').disabled");

        await Choose("downloads");
        await Edit("DownloadRoot", Path.Combine(root, "web temporary downloads"));
        await Wait(() => store.Load().DownloadRoot.EndsWith("web temporary downloads"));
        await Edit("DownloadRoot", Path.Combine(root, "changed downloads"));
        await Wait(() => store.Load().DownloadRoot.EndsWith("changed downloads"));
        await Edit("DownloadBandwidthMBps", "-1");
        await Until("!!document.querySelector('.settings-error')");
        Check(store.Load().DownloadBandwidthMBps == 75, "invalid bandwidth preserves the saved value");
        await Edit("DownloadBandwidthMBps", "80"); await Wait(() => store.Load().DownloadBandwidthMBps == 80);
        await Edit("DownloadBandwidthMBps", "75"); await Wait(() => store.Load().DownloadBandwidthMBps == 75);
        await Choose("installation");
        await Edit("InstalledGamesRoot", Path.Combine(root, "web temporary installed")); await Wait(() => store.Load().InstalledGamesRoot.EndsWith("web temporary installed"));
        await Edit("InstalledGamesRoot", Path.Combine(root, "installed separately")); await Wait(() => store.Load().InstalledGamesRoot.EndsWith("installed separately"));
        Check(store.Load().DownloadRoot.EndsWith("changed downloads"), "graphical installed root remains independent of Downloads");
        await Choose("passwords");
        await Assert("!document.querySelector('.settings-passwords').textContent.includes('synthetic secret') && !document.querySelector('.settings-passwords input[type=text]')", "saved passwords stay masked");
        await Script("document.querySelector('.orbit-settings-content input[type=password]').value='ignored'");
        // React's native setter path exercises the form, rather than sending an API-only save.
        await SetInput(".orbit-settings-content input[type=password]", " Web Secret ");
        await Script("document.querySelector('.orbit-settings-content form').requestSubmit()");
        await Wait(() => store.Load().ArchivePasswords.Count == 4);
        Check(store.Load().ArchivePasswords.Last() == " Web Secret ", "masked web password preserves exact spaces and case");
        await Until("document.querySelectorAll('.settings-passwords li').length===4");
        await Script("[...document.querySelectorAll('.settings-passwords button')].find(button=>button.textContent==='Remove password 4').click()");
        await Wait(() => store.Load().ArchivePasswords.Count == 3);
        await Assert("document.querySelector('.orbit-settings-content input[type=password]').value===''", "submitted password is cleared");
        var response = await AsyncScript("fetch('/api/v1/windows/settings').then(r=>r.json()).then(s=>({masked:!JSON.stringify(s).includes('synthetic secret'),count:s.passwords.length}))");
        Check(response.Contains("\"masked\":true"), "settings state never serializes saved password bytes");
        await Assert("fetch('/api/v1/windows/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind:'field',name:'Interface',value:'Classic Windows'})}).then(r=>r.status===400)", "settings endpoint rejects fields outside its allowlist");
        await Choose("emulators");
        await Edit("ArgumentWidth", "1600"); await Wait(() => model.ArgumentWidth == "1600");
        Check(store.Load().KytyArguments?.ScreenWidth != 1600, "graphical arguments stay drafts until Apply");
        await Edit("ArgumentWidth", "1440"); await Wait(() => model.ArgumentWidth == "1440");
        await Script("document.querySelector('[data-settings-action=applyArguments]').click()");
        await Wait(() => store.Load().KytyArguments?.ScreenWidth == 1440);
        await Choose("metadata");
        await Until("!!document.querySelector('.igdb-settings input[type=password]')");
        await Assert("document.querySelector('.igdb-settings input[type=password]').value==='' && !document.querySelector('[data-windows-action=settings]')", "Metadata uses real IGDB form with blank saved-secret input");
        await SetInput(".igdb-settings input:not([type=password])", "updated-offline-fixture");
        await SetInput(".igdb-settings input[type=password]", "fixture-igdb-secret");
        await Script("document.querySelector('.igdb-settings').requestSubmit()");
        await Wait(() => store.Load().IgdbClientId == "updated-offline-fixture" && store.Load().IgdbClientSecretProtected.Length > 0);
        await Until("document.querySelector('.igdb-settings input[type=password]').value===''");
        var protectedSecret = store.Load().IgdbClientSecretProtected;
        await Script("document.querySelector('.igdb-settings').requestSubmit()");
        await Task.Delay(150);
        Check(store.Load().IgdbClientSecretProtected == protectedSecret, "Metadata blank-secret save retains the existing protected credential");
        await Script("document.querySelector('.settings-breadcrumb button').click()");
        await Until("document.querySelector('[data-settings-category=metadata] .settings-tile-status')?.textContent==='Configured'");
        host.ShowAppearance(); await Until("!!document.querySelector('.orbit-settings-content .iface-card')");
        await Script("document.querySelector('.orbit-settings-content [role=combobox]').focus();document.querySelector('.orbit-settings-content [role=combobox]').scrollIntoView({block:'center'});document.querySelector('.orbit-settings-content [role=combobox]').click()");
        await Until("!!document.querySelector('[role=option]')");
        await Script("[...document.querySelectorAll('[role=option]')].find(option=>option.textContent.trim()==='Fullscreen').click()");
        await Wait(() => store.Load().StartupFullscreen);
        Check(window.WindowStyle != WindowStyle.None, "startup mode saves without changing the current session");
        await Assert("!document.querySelector('[role=dialog]')", "F2 opens the Appearance page rather than another UI mode");
        await Script("[...document.querySelectorAll('.iface-card')].find(button=>button.textContent.includes('Crossbar')).click()");
        await Until("!!document.querySelector('[role=alertdialog]')");
        await Script("document.querySelector('[role=alertdialog] .primary').click()");
        await Until("!!document.querySelector('.app.iface-crossbar .orbit-settings .iface-card') && !document.querySelector('[role=alertdialog]')");
        await AsyncScript("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
        await Assert("JSON.parse(localStorage.getItem('orbit.interface.v1')).name==='crossbar'", "Appearance uses the original confirmed interface persistence");
        await Choose("language");
        await Script("document.querySelector('.language-setting [role=combobox]').focus();document.querySelector('.language-setting [role=combobox]').click()");
        await Until("!!document.querySelector('[role=listbox]')");
        await Assert("document.activeElement===document.querySelector('.language-setting [role=combobox]')", "language dropdown retains focus on its controller trigger");
        await Script("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
        await Until("!document.querySelector('[role=listbox]')");
        await Assert("!document.querySelector('[role=listbox]') && !!document.querySelector('[data-settings-category=language][aria-current=page]')", "dropdown Escape closes the choice before navigating the page");
        await Script("document.querySelector('.language-setting [role=combobox]').focus();document.querySelector('.language-setting [role=combobox]').click()");
        await Until("!!document.querySelector('[role=listbox]')");
        await Reload("[...document.querySelectorAll('[role=option]')].find(option=>option.textContent.trim()==='English').click()");
        await Until("localStorage.getItem('orbit.language.v1')==='en' && !document.querySelector('.orbit-settings')");
        Check(surface.IsVisible && !((TabControl)window.FindName("ClassicTabs")).IsVisible, "language reload keeps the graphical surface: " + model.Status);
        using (var transport = new OrbitFrontendApi(model))
        {
            var endpoint = OrbitFrontendSecurity.Origin + "/api/v1/windows/settings";
            Check(transport.Respond("https://untrusted.invalid/index.html", "GET", endpoint).Status == 403 &&
                transport.Respond(OrbitFrontendSecurity.Document, "POST", endpoint, "{\"kind\":\"field\",\"name\":\"Interface\",\"value\":\"Classic Windows\"}").Status == 400 &&
                transport.Respond(OrbitFrontendSecurity.Document, "POST", endpoint, "{\"kind\":\"action\",\"action\":\"sourceSave\"}").Status == 400,
                "settings transport rejects untrusted documents, unknown fields and malformed source drafts");
        }

        await Reload("localStorage.setItem('orbit.interface.v1',JSON.stringify({name:'cinema',motion:'reduced'}));location.reload()");
        await Until("!!document.querySelector('.app.iface-cinema')"); await Nav("Settings");
        window.WindowState = WindowState.Normal;
        foreach (var (size, width, height) in new[] { ("wide", 1920d, 1080d), ("desktop", 1280d, 800d), ("compact", 920d, 620d) })
        {
            window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(120);
            Check(surface.IsVisible && window.WindowState != WindowState.Minimized, "screenshot target remains visible: " + model.Status);
            await Script("document.querySelector('.settings-breadcrumb button').click()");
            await Until("!!document.querySelector('.settings-home')"); await Capture(size + "-home.png");
            await CheckTileRail();
            foreach (var category in categories)
            {
                await Choose(category);
                await Assert("document.documentElement.scrollWidth<=innerWidth+1 && document.querySelector('.orbit-settings-content').getBoundingClientRect().width>100", "no horizontal page overflow: " + category + " at " + size);
                await Assert("[...document.querySelectorAll('.orbit-settings-panel')].every(panel=>panel.scrollWidth<=panel.clientWidth+1)", "panel contents fit without hidden horizontal overflow: " + category + " at " + size);
                await Capture(size + "-" + category + ".png");
                if (category == "emulators")
                {
                    await Script("document.querySelector('.orbit-settings-content details').open=true;document.querySelector('.orbit-settings-content pre').scrollIntoView({block:'center'})");
                    await Assert("document.querySelector('.orbit-settings-content pre').getBoundingClientRect().height>30", "readable output disclosure: " + category);
                    await Capture(size + "-" + category + "-output.png");
                }
                await Script("document.querySelector('.orbit-settings-content').lastElementChild.scrollIntoView({block:'end'})");
                await Assert("document.querySelector('.orbit-settings-content').lastElementChild.getBoundingClientRect().bottom<=innerHeight+1", "detail bottom reachable: " + category + " at " + size);
            }
        }
        await CheckProgress();
        await Script("window.chrome.webview.postMessage({version:1,command:'fullscreen'})");
        await Wait(() => window.WindowStyle == WindowStyle.None); await Choose("downloads");
        await Key("Escape"); await Until("!!document.querySelector('.settings-home')");
        await Key("Escape"); await Until("!document.querySelector('.orbit-settings')");
        Check(window.WindowStyle == WindowStyle.None && surface.IsVisible, "Back preserves fullscreen and graphical surface");
        await Script("window.chrome.webview.postMessage({version:1,command:'exitFullscreen'})");
        await Wait(() => window.WindowStyle != WindowStyle.None);
        Console.WriteLine("PASS graphical Settings: shared shell in four layouts, ten borderless tiles, Escape/right-click/controller B/focus, real fields/source/IGDB saves, appearance/language, masking, Apply semantics, compact/desktop views and updater output");

        async Task CheckProgress()
        {
            await Choose("emulators"); await Wait(() => model.CanManageSharpEmu && !model.IsCheckingKytyVersion);
            var managed = Path.Combine(root, "managed-sharp-fixture"); Directory.CreateDirectory(Path.Combine(managed, "source", ".git"));
            var patch = Path.Combine(managed, "fixture.patch"); File.WriteAllText(patch, "synthetic patch");
            var factory = model.CreateSharpUpdater; var fetching = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            model.CreateSharpUpdater = output => new SharpEmuUpdater(managed, patch, output) { RunProcess = (info, line) => {
                var command = info.ArgumentList[0];
                if (command == "rev-parse") line(info.WorkingDirectory); else if (command == "remote") line(SharpEmuUpdater.OfficialOrigin);
                else if (command == "branch") line("main"); else if (command == "ls-remote") line(new string('c', 40) + "\trefs/heads/main");
                else if (command == "fetch") { line("Fixture build output " + new string('x', 220)); return fetching.Task; }
                return Task.FromResult(0);
            } };
            try
            {
                await Script("document.querySelector('[data-settings-action=checkSharp]').click()");
                await Wait(() => !model.IsCheckingSharpEmu && model.SharpEmuAvailableRevision == new string('c', 40));
                await Until("!document.querySelector('[data-settings-action=updateSharp]').disabled");
                await Script("document.querySelector('[data-settings-action=updateSharp]').click()");
                await Wait(() => model.IsUpdatingSharpEmu && model.SharpEmuUpdateOutput.Contains("Fixture build output"));
                await Until("document.querySelector('[data-settings-action=checkSharp]').disabled && document.querySelector('[data-settings-action=updateSharp]').disabled && document.querySelector('[data-setting=SharpEmuPath]').disabled && document.querySelector('.orbit-settings-content pre').textContent.includes('Fixture build output')");
                await Assert("fetch('/api/v1/windows/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind:'action',action:'updateSharp'})}).then(r=>r.status===409)", "transport rejects competing update actions during the real fixture operation");
                await Script("document.querySelector('.orbit-settings-content details').open=true;document.querySelector('.orbit-settings-content pre').scrollIntoView({block:'center'})");
                await Capture("update-progress-output.png");
                fetching.TrySetResult(23); await Wait(() => !model.IsUpdatingSharpEmu);
                await Until("!document.querySelector('[data-settings-action=checkSharp]').disabled");
            }
            finally { fetching.TrySetResult(23); await Wait(() => !model.IsUpdatingSharpEmu); model.CreateSharpUpdater = factory; }
        }
        async Task CheckTileRail()
        {
            await Script("document.querySelector('.settings-breadcrumb button').click();document.querySelector('[data-settings-category=sources]').focus()");
            await Until("document.querySelector('[data-settings-category=passwords] .settings-tile-status')?.textContent==='3 saved'");
            await Assert("[...document.querySelectorAll('[data-settings-category]')].every(tile=>Math.abs(tile.offsetWidth/tile.offsetHeight-2)<.02 && !!tile.querySelector('.settings-tile-status') && (()=>{const box=tile.getBoundingClientRect(),caption=tile.querySelector('.settings-tile-caption').getBoundingClientRect();return caption.left>box.left && caption.bottom<box.bottom && caption.top>=box.top})()) && new Set([...document.querySelectorAll('[data-settings-category]')].map(tile=>tile.offsetTop)).size>1", "home has multiple rows of 2:1 cards with title and live status inside");
            await Assert("[...document.querySelectorAll('[data-settings-category]')].every(tile=>tile.querySelector('.settings-tile-art[data-variant=wide] image') && Math.abs(tile.querySelector('.settings-tile-art svg').getBoundingClientRect().height-tile.getBoundingClientRect().height)<1 && Number(getComputedStyle(tile).opacity)===1)", "all home tiles use full-bleed wide artwork at full opacity");
            await Script("document.querySelector('[data-settings-category=sources]').focus()");
            await Key("ArrowDown");
            await Assert("document.activeElement?.dataset.settingsCategory!=='sources' && document.activeElement?.hasAttribute('data-settings-category')", "Down moves to the next grid row");
            await Key("ArrowUp"); await Assert("document.activeElement?.dataset.settingsCategory==='sources'", "Up returns to the previous grid row");
            foreach (var category in categories.Skip(1).Append("sources"))
            {
                await Key("ArrowRight");
                await Assert($"document.activeElement?.dataset.settingsCategory==='{category}' && (()=>{{const tile=document.activeElement.getBoundingClientRect(),rail=document.querySelector('.settings-tiles').getBoundingClientRect();return tile.left>=rail.left && tile.right<=rail.right+1}})()", "category rail moves focus into view and wraps: " + category);
            }
            await Key("ArrowLeft"); await Assert("document.activeElement?.dataset.settingsCategory==='about'", "category rail wraps backwards");
            await Choose("about");
            await AsyncScript("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
            await Assert("[...document.querySelectorAll('[data-settings-category]')].every(tile=>Math.abs(tile.offsetWidth/tile.offsetHeight-1)<.02 && !tile.querySelector('.settings-tile-status') && (()=>{const box=tile.getBoundingClientRect(),caption=tile.querySelector('.settings-tile-caption').getBoundingClientRect();return caption.left>box.left && caption.bottom<box.bottom && caption.top>=box.top})()) && new Set([...document.querySelectorAll('[data-settings-category]')].map(tile=>tile.offsetTop)).size===1", "detail has one horizontal row of 1:1 cards with inset titles and no status");
            await Assert("getComputedStyle(document.querySelector('.settings-tiles')).scrollbarWidth==='none'", "detail rail has no visible native scrollbar");
            await Assert("[...document.querySelectorAll('[data-settings-category]')].every(tile=>tile.querySelector('.settings-tile-art[data-variant=square] image') && Math.abs(Number(getComputedStyle(tile).opacity)-(tile.getAttribute('aria-current')==='page'?1:.9))<.001)", "all detail tiles use square artwork; active is 100 percent and inactive is 90 percent opaque");
            await Script("document.querySelector('[data-settings-category=sources]').focus();document.activeElement.blur();document.querySelector('.settings-tiles').scrollLeft=0");
            await Assert("(()=>{const rail=document.querySelector('.settings-tiles');const before=rail.scrollLeft;const event=new WheelEvent('wheel',{deltaY:150,bubbles:true,cancelable:true});rail.dispatchEvent(event);return event.defaultPrevented && rail.scrollLeft>before})()", "mouse wheel moves the overflowing category rail");
            await Script("document.querySelector('.settings-breadcrumb button').click()");
            await AsyncScript("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
            await Script("document.querySelector('[data-settings-category=sources]').focus();document.activeElement.blur();document.querySelector('.settings-breadcrumb').scrollIntoView({block:'start'})");
        }
        Task<string> Script(string script) => host.Browser.ExecuteScriptAsync(script);
        async Task Reload(string script)
        {
            // A mounted React page can precede NavigationCompleted. Do not interrupt
            // the language reload with the next layout reload during acceptance.
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
            {
                if (args.IsSuccess) completed.TrySetResult();
                else completed.TrySetException(new IOException("Settings test navigation failed: " + args.WebErrorStatus));
            }
            host.Browser.CoreWebView2.NavigationCompleted += NavigationCompleted;
            try { await Script(script); await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Browser.CoreWebView2.NavigationCompleted -= NavigationCompleted; }
        }
        async Task Until(string expression) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (await Script(expression) != "true") await Task.Delay(50, timeout.Token); }
        async Task<string> AsyncScript(string expression) { using var result = JsonDocument.Parse(await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }))); return result.RootElement.GetProperty("result").GetProperty("value").GetRawText(); }
        async Task Assert(string expression, string message) { Check(await AsyncScript(expression) == "true", message); }
        async Task Key(string key) { host.Browser.Focus(); await Script($"(document.activeElement||document.body).dispatchEvent(new KeyboardEvent('keydown',{{key:'{key}',bubbles:true,cancelable:true}}))"); }
        async Task PadBack() { window.Activate(); host.Browser.Focus(); await Until("document.hasFocus()"); await Task.Delay(150); await Script("window.testPad.buttons[1]={pressed:true,value:1}"); await Task.Delay(150); await Script("window.testPad.buttons[1]={pressed:false,value:0}"); await Task.Delay(150); }
        async Task Nav(string name) { await Until($"[...document.querySelectorAll('.main-nav button')].some(b=>b.textContent.trim()==='{name}')"); await Script($"[...document.querySelectorAll('.main-nav button')].find(b=>b.textContent.trim()==='{name}').click()"); if (name == "Settings") await Until("!!document.querySelector('.settings-home') && !!document.querySelector('[data-settings-category=about]')"); }
        async Task Choose(string category) { await Script($"document.querySelector('[data-settings-category={category}]').click()"); await Until($"!!document.querySelector('[data-settings-category={category}][aria-current=page]') && !!document.querySelector('.orbit-settings-content > :first-child')"); await Script("document.querySelector('.settings-breadcrumb').scrollIntoView({block:'start'})"); }
        async Task SetInput(string selector, string value) { await Script($"(()=>{{let input=document.querySelector({JsonSerializer.Serialize(selector)});input.focus();Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,{JsonSerializer.Serialize(value)});input.dispatchEvent(new Event('input',{{bubbles:true}}));}})()"); }
        async Task Edit(string name, string value) { await SetInput($"[data-setting={name}]", value); await Script("document.activeElement.blur()"); }
        async Task Capture(string name)
        {
            if (Environment.GetEnvironmentVariable("PS5_SETTINGS_EVIDENCE") is not { Length: > 0 } evidence) return;
            Directory.CreateDirectory(evidence); await Task.Delay(80);
            using var capture = JsonDocument.Parse(await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"captureBeyondViewport\":false}").WaitAsync(TimeSpan.FromSeconds(20)));
            await File.WriteAllBytesAsync(Path.Combine(evidence, "graphical-" + name), Convert.FromBase64String(capture.RootElement.GetProperty("data").GetString()!));
        }
    }
    private static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!condition()) await Task.Delay(50, timeout.Token); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
