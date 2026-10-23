using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class CoverGridTests
{
    public static async Task RunAsync(bool previewOnly = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5CoverGrid-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
        string[] names = ["Astra Drift", "Blue Horizon", "Crimson Run", "Distant Signal", "Echo Valley", "Fallen Star", "Glass Harbour", "Hidden World", "Iron Circuit", "Jade Forest", "Kindred Skies", "Lunar Tide", "Midnight Road", "Neon Summit", "Orbit Dawn", "Quiet Lands"];
        string[] statuses = ["logo", "main-menu", "in-game", "doesnt-boot", "untested"];
        var items = names.Select((title, index) =>
        {
            var folder = Path.Combine(installed, title); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "eboot.bin"), "synthetic");
            return new LocalLibraryEntry { Title = title, ContentPath = folder, Version = "1.00", Extraction = "Extracted", Integrity = "Not verified",
                CoverUrl = index == 15 ? "" : $"https://covers.invalid/fixture-{index}.svg", Classification = index == 15 ? "Missing launch content" : "Ready for launch attempt",
                InstalledGame = new() { Directory = folder, TitleId = $"PPSA{index + 10000}", LaunchEligible = index != 15 } };
        }).ToList();
        var emulator = Path.Combine(root, "kyty_emulator.exe"); File.WriteAllText(emulator, "KytyPS5-2026-10-17-1234567\0");
        var store = new SettingsStore(Path.Combine(root, "settings"));
        foreach (var item in items)
        {
            var job = new InstallationJob { Title = item.Title, Destination = item.ContentPath, State = InstallationState.Completed };
            item.InstalledGame!.InstallationId = job.Id;
            new InstallationJournal(Path.Combine(store.DirectoryPath, "installations")).Save(job);
        }
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = installed, Library = items, EmulatorPath = emulator });
        var model = new MainViewModel(store);
        for (var index = 0; index < 5; index++) model.SetLibraryCompatibility(model.Library[index], statuses[index]);
        model.Settings.CompatibilityTestVersions.Remove("PPSA10001"); store.Save(model.Settings); // Legacy local result has no build metadata.
        if (previewOnly)
            model.Settings.CompatibilityTestVersions["PPSA10000"] = new("KytyPS5-2026-10-16-1234567", DateTimeOffset.UtcNow);
        await model.RefreshCompatibilityAsync(_ => Task.FromResult(items.Select((item, index) => (item, index)).ToDictionary(pair => pair.item.InstalledGame!.TitleId, pair => pair.index < 5 ? "in-game" : statuses[(pair.index - 5) % statuses.Length])));
        var saved = JsonSerializer.Serialize(store.Load());
        using var host = new OrbitFrontendHost();
        var window = new Window { Content = host, Width = 1280, Height = 800 };
        var actions = new List<OrbitFrontendAction>(); host.ActionRequested += actions.Add;
        try
        {
            window.Show(); window.Activate(); await host.InitializeAsync(model); host.Browser.Focus();
            await Script("Object.defineProperty(document,'hasFocus',{configurable:true,value:()=>true})");
            await Key("Escape");
            await Check("document.querySelector('.main-nav [aria-current=page]')?.textContent==='Discover' && !!document.querySelector('#root .app')", "Back with no visited-page history is a no-op");
            host.Browser.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith("https://covers.invalid/fixture-", StringComparison.Ordinal)) return;
                var index = int.Parse(Path.GetFileNameWithoutExtension(new Uri(e.Request.Uri).AbsolutePath).Replace("fixture-", ""));
                // Original geometric test artwork, served in-process. No licensed assets or network requests.
                string[] colors = ["#425783", "#357c91", "#9b4f67", "#756299", "#548578"];
                var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='400' height='600'><rect width='400' height='600' fill='#111b2d'/><path d='M0 100L400 360V600H0Z' fill='{colors[index % colors.Length]}'/><circle cx='290' cy='170' r='110' fill='{colors[(index + 1) % colors.Length]}'/><path d='M0 480L400 210' stroke='#d7e8ff' stroke-opacity='.35' stroke-width='3'/><text x='30' y='55' fill='#e3edff' font-family='sans-serif' font-size='16' letter-spacing='3'>TEST COLLECTION</text><text x='30' y='530' fill='white' font-family='sans-serif' font-size='30' font-weight='bold'>{names[index].ToUpperInvariant()}</text><text x='30' y='566' fill='#d7e8ff' font-family='sans-serif' font-size='14'>SYNTHETIC UI FIXTURE · {index + 1:00}</text></svg>";
                e.Response = host.Browser.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(svg)), 200, "OK", "Content-Type: image/svg+xml\r\n");
            };
            host.NavigatePage("Browse"); await Wait("!!document.querySelector('.browse')");
            host.NavigatePage("Library"); await Wait("document.querySelectorAll('.windows-library-card').length===16");
            await Script("window.testFocus=true;Object.defineProperty(document,'hasFocus',{configurable:true,value:()=>window.testFocus});window.testPad={buttons:Array.from({length:17},()=>({pressed:false,value:0})),axes:[0,0,0,0]};Object.defineProperty(navigator,'getGamepads',{configurable:true,value:()=>[window.testPad]})");
            await Task.Delay(100);
            if (previewOnly)
            {
                await Script("document.querySelector('.library-play-surface').focus()");
                await Wait("!!document.querySelector('.library-preview')");
                await Check("!document.querySelector('.library-detail-list').textContent.includes('KytyPS5')", "current/unknown website results leave version note blank");
                await Script("document.activeElement?.blur();[...document.querySelectorAll('.library-compatibility-source button')].find(x=>x.textContent==='Local').click()");
                await Wait("document.querySelectorAll('.library-compatibility-source button')[1].getAttribute('aria-pressed')==='true'");
                await Script("document.querySelector('.library-play-surface').focus()");
                await Wait("document.querySelector('.library-detail-list')?.textContent.includes('An older version of KytyPS5')===true");
                await Check("document.querySelector('.library-compatibility-pill').textContent.trim()==='Logo'", "older version is absent from status badge");
                await Check("!document.querySelector('.library-preview').textContent.includes('version tested:')", "tested build details are replaced");
                await Script("window.previewScroll=document.querySelector('.library-poster').parentElement;while(window.previewScroll.parentElement&&!(window.previewScroll.scrollHeight>window.previewScroll.clientHeight&&/auto|scroll/.test(getComputedStyle(window.previewScroll).overflowY)))window.previewScroll=window.previewScroll.parentElement;if(window.previewScroll===document.body||window.previewScroll===document.documentElement)window.previewScroll=document.scrollingElement;window.previewScroll.scrollBy(0,document.querySelector('.library-poster').getBoundingClientRect().top+50)");
                await Wait("(()=>{const a=document.querySelector('.library-poster').getBoundingClientRect(),p=document.querySelector('.library-preview')?.getBoundingClientRect();return p&&a.top<0&&Math.abs(a.top-p.top)<1&&Math.abs(a.right-p.left)<1})()");
                await Check("document.querySelectorAll('.library-preview').length===1", "scroll keeps one attached preview");
                await Script("window.previewScroll.scrollTo(0,0);document.querySelectorAll('.library-play-surface')[1].focus()");
                await Wait("document.querySelector('.library-preview h2')?.textContent==='Blue Horizon'");
                await Check("!document.querySelector('.library-detail-list').textContent.includes('KytyPS5')", "unknown version leaves note blank");
                await Script("document.querySelectorAll('.library-play-surface')[2].focus()");
                await Wait("document.querySelector('.library-preview h2')?.textContent==='Crimson Run'");
                await Check("!document.querySelector('.library-detail-list').textContent.includes('KytyPS5')", "current version leaves note blank");
                Console.WriteLine("PASS preview: older/current/unknown wording, status badge, and thumbnail alignment while scrolling");
                return;
            }
            const string cards = ".windows-library-card";
            await Check("document.querySelector('.library-status-chip').dataset.source==='My test' && document.querySelector('.library-status-chip').dataset.compatibility==='logo'", "worse local result wins over website in-game");
            await Check("[...document.querySelectorAll('.library-status-chip')].slice(0,4).every(x=>x.dataset.source==='My test')", "all meaningful local statuses preserve provenance");
            await Check("document.querySelectorAll('.library-status-chip')[4].dataset.source==='Website' && document.querySelectorAll('.library-status-chip')[4].dataset.compatibility==='in-game'", "local untested falls back to website");
            await Check("document.querySelectorAll('.library-status-chip')[9].dataset.source==='Not tested'", "unknown/untested is neutral");
            await Check("getComputedStyle(document.querySelectorAll('.library-status-chip')[0]).backgroundColor!==getComputedStyle(document.querySelectorAll('.library-status-chip')[5]).backgroundColor", "same status has distinct local and website colours");
            await Check("[...document.querySelectorAll('.library-status-chip')].every(x=>x.textContent.trim().length>2 && x.title)", "status is conveyed by text and source tooltip");
            await Check("document.querySelectorAll('.library-card-body, .windows-library-card [data-windows-action=deleteLibrary],.windows-library-card [data-windows-action=openFolder]').length===0", "collapsed grid hides diagnostics and secondary actions");
            await Check("document.querySelectorAll('.library-emulator').length===1", "current emulator build appears once in library header");
            await Check("[...document.querySelectorAll('.library-status-chip')].every(x=>{const rgb=s=>s.match(/[0-9.]+/g).slice(0,3).map(Number);const luminance=s=>rgb(s).map(x=>{x/=255;return x<=.04045?x/12.92:Math.pow((x+.055)/1.055,2.4)}).reduce((a,x,i)=>a+x*[.2126,.7152,.0722][i],0);const c=getComputedStyle(x),a=luminance(c.color),b=luminance(c.backgroundColor);return (Math.max(a,b)+.05)/(Math.min(a,b)+.05)>=4.5})", "local, website and neutral chips meet normal-text contrast");
            using (var api = new OrbitFrontendApi(model))
            {
                var route = OrbitFrontendSecurity.Origin + "/api/v1/windows/details";
                if (api.Respond(OrbitFrontendSecurity.Document, "POST", route, JsonSerializer.Serialize(new { id = items[0].Id, path = "settings.json" })).Status != 400 ||
                    api.Respond(OrbitFrontendSecurity.Document, "POST", route, "{\"id\":\"missing\"}").Status != 409)
                    throw new Exception("Installation details must accept only a retained library ID");
            }

            foreach (var size in new[] { (1920, 1080, 5, 7), (1280, 720, 4, 5), (720, 800, 2, 3), (390, 760, 1, 2) })
            {
                await Viewport(size.Item1, size.Item2);
                await Check($"(()=>{{const a=[...document.querySelectorAll('{cards}')],top=a[0].getBoundingClientRect().top,n=a.filter(x=>Math.abs(x.getBoundingClientRect().top-top)<2).length;return n>={size.Item3}&&n<={size.Item4}&&document.documentElement.scrollWidth<=innerWidth}})()", $"responsive columns and no horizontal overflow at {size.Item1}");
                await Check("(()=>{const r=document.querySelector('.library-poster').getBoundingClientRect();return Math.abs(r.width/r.height-2/3)<0.01})()", "portrait covers retain 2:3 ratio");
                await Capture($"library-{size.Item1}-collapsed.png");
            }
            await Viewport(1280, 720);
            await Script("document.activeElement?.blur();document.querySelectorAll('.library-play-surface')[5].scrollIntoView({block:'center'});document.querySelectorAll('.windows-library-card')[5].dispatchEvent(new PointerEvent('pointerover',{bubbles:true,pointerType:'mouse'}))");
            await Task.Delay(80); await Check("!document.querySelector('.library-preview')", "pointer preview has a hover delay");
            await Wait("document.querySelector('.library-preview')?.textContent.includes('Website')===true");
            await Capture("library-hover-website.png");
            await Script("document.querySelectorAll('.windows-library-card')[5].dispatchEvent(new PointerEvent('pointerout',{bubbles:true,pointerType:'mouse',relatedTarget:document.body}))");
            await Wait("!document.querySelector('.library-preview')");
            await Script("document.querySelector('.library-play-surface').focus();window.testRects=[...document.querySelectorAll('.windows-library-card')].map(x=>JSON.stringify(x.getBoundingClientRect().toJSON()))");
            await Wait("document.querySelectorAll('.library-preview').length===1");
            await Check("[...document.querySelectorAll('.windows-library-card')].every((x,i)=>JSON.stringify(x.getBoundingClientRect().toJSON())===window.testRects[i])", "preview never reflows grid");
            await Check("document.querySelector('.library-preview').parentElement===document.body", "portal escapes scrolling container clipping");
            await Check("document.querySelector('.library-preview').textContent.includes('My test') && document.querySelector('.library-preview').textContent.includes('Website')", "preview separates local and website results");
            await Capture("library-keyboard-preview.png");
            await Script("window.dispatchEvent(new Event('windows-appearance'))"); await Wait("!!document.querySelector('[role=dialog]')");
            await Check("!document.querySelector('.library-preview') || getComputedStyle(document.querySelector('.library-preview')).display==='none'", "preview cannot cover an application modal");
            await Key("Escape"); await Wait("!document.querySelector('[role=dialog]')");
            await Key("ArrowRight"); await Wait("document.activeElement===document.querySelectorAll('.library-play-surface')[1]");
            await Check("document.querySelectorAll('.library-preview').length===1 && document.querySelector('.library-preview').textContent.includes('Blue Horizon')", "spatial focus switches a single preview");
            await Key("ArrowDown");
            await Check("(()=>{const a=[...document.querySelectorAll('.windows-library-card')],n=a.filter(x=>Math.abs(x.getBoundingClientRect().top-a[0].getBoundingClientRect().top)<2).length;return document.activeElement===a[n+1].querySelector('.library-play-surface')})()", "Down navigates corresponding rendered column");
            await Key("Escape"); await Wait("!document.querySelector('.library-preview')");
            await Check("!!document.querySelector('.windows-library')", "first Back dismisses preview without leaving library");
            await Script("document.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
            await Wait("!document.querySelector('.windows-library') && !!document.querySelector('.browse')");
            host.NavigatePage("Library"); await Wait("!!document.querySelector('.windows-library')");
            await Check("document.activeElement?.classList.contains('library-play-surface')", "return restores remembered game focus");
            await Script("document.querySelector('.library-more-button').focus()");
            await Key("Enter"); await Wait("!!document.querySelector('[role=dialog]')");
            if (actions.Count != 0) throw new Exception("More launched a game");
            await Capture("library-more.png");
            await Key("Escape"); await Wait("!document.querySelector('[role=dialog]')");
            await Check("document.activeElement?.classList.contains('library-more-button')", "Back restores More focus");
            await Script("document.querySelector('.library-more-button').click()"); await Wait("!!document.querySelector('[data-windows-action=libraryDetails]')");
            await Script("document.querySelector('[data-windows-action=libraryDetails]').click()"); await Wait("document.querySelectorAll('.library-installation-details dt').length>0");
            await Capture("library-installation-details.png");
            await Key("Escape"); await Wait("!document.querySelector('[role=dialog]') && !!document.querySelector('.windows-library')");
            await Check("document.activeElement?.classList.contains('library-more-button')", "details Back restores library and invoking game");
            if (actions.Count != 0) throw new Exception("Details opened a launch action");
            await Script("document.querySelector('.library-play-surface').focus()");
            await Pad(0, true); await WaitActions(1); await Task.Delay(350);
            if (actions.Count != 1 || actions[0].Action != "launch") throw new Exception("A must launch exactly once");
            await Pad(0, false); await Task.Delay(80); actions.Clear();
            // Focus loss with held A, B, X and a stick must never activate on return.
            foreach (var button in new[] { 0, 1, 2 })
            {
                await Script($"window.testFocus=false;window.dispatchEvent(new Event('blur'));window.testPad.buttons[{button}].pressed=true");
                await Task.Delay(100); await Script("window.testFocus=true"); await Task.Delay(300);
                await Check("!document.querySelector('[role=dialog]') && !!document.querySelector('.windows-library')", "held controller button is ignored across focus return");
                if (actions.Count != 0) throw new Exception("Background/return input dispatched a launch");
                await Pad(button, false); await Task.Delay(80);
            }
            await Script("window.testFocus=false;window.dispatchEvent(new Event('blur'));window.testPad.axes[1]=1;window.testBefore=document.activeElement");
            await Task.Delay(100); await Script("window.testFocus=true"); await Task.Delay(300);
            await Check("document.activeElement===window.testBefore", "held stick cannot navigate on focus return");
            await Script("window.testPad.axes[1]=0"); await Task.Delay(80);
            await Pad(2, true); await Wait("!!document.querySelector('[role=dialog]')"); await Pad(2, false);
            if (actions.Count != 0) throw new Exception("Controller X must open More without launching");
            await Pad(1, true); await Wait("!document.querySelector('[role=dialog]')"); await Pad(1, false); await Task.Delay(80);
            await Script("document.querySelector('.library-play-surface').focus()");
            await Key("Enter"); await Key("Enter", true); await WaitActions(1);
            if (actions.Count != 1) throw new Exception("Repeated Enter launched twice"); actions.Clear();
            await Script("document.querySelector('.library-play-surface').click();document.querySelector('.library-play-surface').click()"); await WaitActions(1);
            if (actions.Count != 1 || actions[0].Action != "launch") throw new Exception("Double primary mouse activation bypassed pending guard"); actions.Clear();
            await Script("document.querySelectorAll('.library-play-surface')[15].focus();document.querySelectorAll('.library-play-surface')[15].click()");
            await Wait("!!document.querySelector('.library-unavailable')");
            if (actions.Count != 0) throw new Exception("Ineligible card dispatched launch");
            await Check("(()=>{const r=document.querySelector('.library-preview').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})()", "edge preview stays within viewport");
            await Capture("library-edge-unavailable.png");
            await Script("document.querySelector('.library-toolbar input').focus();Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(document.querySelector('.library-toolbar input'),'Astra');document.querySelector('.library-toolbar input').dispatchEvent(new Event('input',{bubbles:true}))");
            await Wait("document.querySelectorAll('.windows-library-card').length===1");
            await Key("ArrowLeft"); await Key("Enter");
            if (actions.Count != 0) throw new Exception("Search editing launched a game");
            await Script("Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(document.querySelector('.library-toolbar input'),'');document.querySelector('.library-toolbar input').dispatchEvent(new Event('input',{bubbles:true}));document.querySelectorAll('.library-toolbar select')[1].value='logo';document.querySelectorAll('.library-toolbar select')[1].dispatchEvent(new Event('change',{bubbles:true}))");
            await Wait("document.querySelectorAll('.library-status-chip').length>1 && [...document.querySelectorAll('.library-status-chip')].every(x=>x.dataset.compatibility==='logo')");
            await Script("document.querySelectorAll('.library-toolbar select')[1].value='';document.querySelectorAll('.library-toolbar select')[1].dispatchEvent(new Event('change',{bubbles:true}))");
            await Wait("document.querySelectorAll('.windows-library-card').length===16");
            await model.RefreshCompatibilityAsync(_ => Task.FromResult(items.ToDictionary(item => item.InstalledGame!.TitleId, _ => "in-game")));
            await Wait("document.querySelectorAll('.library-status-chip')[5].dataset.compatibility==='in-game'");
            await Check("document.querySelector('.library-status-chip').dataset.compatibility==='logo' && document.querySelector('.library-status-chip').dataset.source==='My test'", "website refresh changes website chips but preserves local authority");
            await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}");
            await Script("document.querySelector('.library-play-surface').focus()"); await Wait("!!document.querySelector('.library-preview')");
            await Check("getComputedStyle(document.querySelector('.library-preview')).animationName==='none'", "reduced motion disables preview animation");
            if (JsonSerializer.Serialize(store.Load()) != saved) throw new Exception("Presentation/input changed stored records");
            foreach (var theme in new[] { "panorama", "cinema", "crossbar", "store" })
            {
                await Script("localStorage.setItem('orbit.interface.v1',JSON.stringify({name:" + JsonSerializer.Serialize(theme) + ",motion:'reduced'}));location.reload()");
                await Wait("!!document.querySelector('.app.iface-" + theme + "')");
                host.NavigatePage("Library"); await Wait("document.querySelectorAll('.windows-library-card').length===16");
                await Check("document.documentElement.scrollWidth<=innerWidth && document.querySelector('.library-header h1').textContent.startsWith('My Library')", "library remains usable in " + theme);
                if (theme == "panorama") await Check("getComputedStyle(document.querySelector('.pn-titles')).display==='none'", "Panorama library has one compact header");
                await Capture("library-" + theme + ".png");
            }
        }
        finally
        {
            window.Close(); host.Dispose(); model.Dispose();
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            { try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
        }
        Console.WriteLine("PASS cover grid: provenance, responsive layout, portal/focus, Back history, exact action routing, controller focus/release and unchanged records");
        Task<string> Script(string code) => host.Browser.ExecuteScriptAsync(code);
        async Task Check(string code, string message) { if (await Script(code) != "true") throw new Exception(message + ": " + await Script("document.body.innerText.slice(-700)")); }
        async Task Wait(string code) { var deadline = DateTime.UtcNow.AddSeconds(15); while (DateTime.UtcNow < deadline) { if (await Script(code) == "true") return; await Task.Delay(60); } throw new Exception("Cover grid timeout: " + code + " " + await Script("document.body.innerText.slice(-700)")); }
        async Task WaitActions(int count) { var deadline = DateTime.UtcNow.AddSeconds(5); while (actions.Count < count && DateTime.UtcNow < deadline) await Task.Delay(50); if (actions.Count != count) throw new Exception($"Expected {count} actions, got {actions.Count}"); }
        async Task Key(string key, bool repeat = false)
        {
            await Script($"document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{{key:{JsonSerializer.Serialize(key)},repeat:{repeat.ToString().ToLowerInvariant()},bubbles:true,cancelable:true}}))");
            if (!repeat) await Script($"document.activeElement.dispatchEvent(new KeyboardEvent('keyup',{{key:{JsonSerializer.Serialize(key)},bubbles:true}}))");
            await Task.Delay(80);
        }
        Task<string> Pad(int index, bool pressed) => Script($"window.testPad.buttons[{index}].pressed={pressed.ToString().ToLowerInvariant()};window.testPad.buttons[{index}].value={(pressed ? 1 : 0)}");
        async Task Viewport(int width, int height)
        {
            await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", JsonSerializer.Serialize(new { width, height, deviceScaleFactor = 1, mobile = false }));
            await Wait($"innerWidth==={width} && innerHeight==={height}"); await Task.Delay(200);
        }
        async Task Capture(string name)
        {
            var evidence = Environment.GetEnvironmentVariable("PS5_ORBIT_EVIDENCE"); if (string.IsNullOrWhiteSpace(evidence)) return;
            Directory.CreateDirectory(evidence);
            await Task.Delay(200);
            var response = await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
            using var json = JsonDocument.Parse(response); await File.WriteAllBytesAsync(Path.Combine(evidence, name), Convert.FromBase64String(json.RootElement.GetProperty("data").GetString()!));
        }
    }
}
