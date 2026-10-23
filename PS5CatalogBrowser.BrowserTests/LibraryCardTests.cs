using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class LibraryCardTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5LibraryCards-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
        var source = new CatalogSource { Id = "cards", Name = "Fixture" };
        LocalLibraryEntry Item(string title) { var folder = Path.Combine(installed, title); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "eboot.bin"), "synthetic");
            return new() { Title = title, ContentPath = folder, SourceId = source.Id, CatalogEntryId = title, Classification = "Ready for launch attempt", Extraction = "Extracted",
                InstalledGame = new() { TitleId = title == "Reported fixture" ? "PPSA03647" : "", Directory = folder, LaunchEligible = true } }; }
        var first = Item("Reported fixture"); var disposable = Item("Deletion fixture");
        var store = new SettingsStore(Path.Combine(root, "settings"));
        foreach (var item in new[] { first, disposable })
        {
            var job = new InstallationJob { Title = item.Title, Destination = item.ContentPath, State = InstallationState.Completed };
            item.InstalledGame!.InstallationId = job.Id;
            new InstallationJournal(Path.Combine(store.DirectoryPath, "installations")).Save(job);
        }
        var emulatorFolder = Path.Combine(root, "emulator"); Directory.CreateDirectory(emulatorFolder);
        var executable = Path.Combine(emulatorFolder, "kyty_emulator.exe");
        // Exercise an embedded build split across the reader's chunk boundary without running a process.
        File.WriteAllText(executable, new string('x', 65520) + "KytyPS5-2026-10-16-5dec523\0");
        store.Save(new() { Sources = [source], DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = installed, Library = [first, disposable], EmulatorPath = executable,
            CompatibilityOverrides = new() { ["PPSA03647"] = "main-menu" } });
        var model = new MainViewModel(store); first = model.Library.Single(item => item.Id == first.Id); disposable = model.Library.Single(item => item.Id == disposable.Id);
        var logDirectory = Path.Combine(store.DirectoryPath, "launch-logs"); Directory.CreateDirectory(logDirectory);
        first.LastLaunchLogId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(logDirectory, first.LastLaunchLogId + ".log"), "Synthetic crash output <script>unsafe()</script>\nExit code: 37");
        store.Save(model.Settings);
        Check(model.CurrentKytyVersion == "KytyPS5-2026-10-16-5dec523" && model.LibraryCompatibilityOverride(first) == "main-menu" && model.LibraryTestVersion(first) is null, "startup reads executable build and preserves legacy custom status without inventing its tested version");
        model.SetLibraryCompatibility(first, "");
        model.Entries.Add(new() { Id = first.CatalogEntryId, SourceId = source.Id, TitleId = "PPSA03647", Title = first.Title, Platform = "PS5", Artifacts = [new() { Id = "fixture", TitleId = "PPSA03647" }] });
        Check(model.LibraryCatalogEntry(new() { InstalledGame = new() { TitleId = "PPSA03647" } })?.Id == first.CatalogEntryId, "inspected title ID can resolve unique catalogue artwork without filename/title guessing");
        await model.RefreshCompatibilityAsync(_ => Task.FromResult(new Dictionary<string, string> { ["PPSA03647"] = "logo" }));
        model.CommunityReports["PPSA03647"] = [new("logo", "KytyPS5-2026-10-15-1234567", "windows", "2026-10-15")];
        var host = new OrbitFrontendHost(); var window = new Window { Content = host, Width = 1120, Height = 850 };
        var events = new List<OrbitFrontendAction>();
        host.ActionRequested += request => { events.Add(request); if (request.Action == "deleteLibrary" && request.DeletionTargets is { } targets)
            {
                var item = model.Library.Single(item => item.Id == request.Id);
                LibraryDeletion.Delete(model.Settings, item, targets, [store.DirectoryPath], _ => false, store.Save,
                    target => Directory.Move(target.Path, Path.Combine(root, "fixture-recycle-" + Guid.NewGuid().ToString("N"))));
                model.Library.Remove(item); model.Status = "Synthetic fixture deleted";
            } };
        try
        {
            using var nativeApi = new OrbitFrontendApi(model);
            var confirmations = new List<OrbitFrontendAction>(); nativeApi.ActionRequested += confirmations.Add;
            (int Status, object Body) Call(string route, object body) => nativeApi.Respond(OrbitFrontendSecurity.Document, "POST", OrbitFrontendSecurity.Origin + "/api/v1/windows/" + route, JsonSerializer.Serialize(body));
            var prepared = Call("delete/prepare", new { id = first.Id });
            var token = JsonSerializer.SerializeToElement(prepared.Body).GetProperty("token").GetString();
            Check(prepared.Status == 200 && Call("delete/confirm", new { id = first.Id, token }).Status == 409, "native backend rejects confirmation before two seconds");
            Check(Call("delete/confirm", new { id = first.Id, token = "wrong" }).Status == 409 &&
                Call("delete/confirm", new { id = first.Id, token, path = first.ContentPath }).Status == 400, "hold token and strict schema reject forged requests");
            await Task.Delay(2050);
            Check(Call("delete/confirm", new { id = first.Id, token }).Status == 202 && confirmations.Single().DeletionTargets!.Single().Path == first.ContentPath &&
                Call("delete/confirm", new { id = first.Id, token }).Status == 409 && Directory.Exists(first.ContentPath), "one-use hold confirmation dispatches captured paths without deleting during API test");
            window.Show(); window.Activate(); await host.InitializeAsync(model); host.Browser.Focus(); host.NavigatePage("Library");
            var card = "[data-windows-id='" + first.Id + "']";
            var deleteCard = "[data-windows-id='" + disposable.Id + "']";
            var management = "[data-library-management='" + first.Id + "']";
            await Wait("!!document.querySelector(" + J(card + " .library-poster img") + ")");
            Check(await Script("(()=>{const card=document.querySelector(" + J(card) + ");const art=card.querySelector('.library-poster').getBoundingClientRect();return Math.abs(art.width/art.height-2/3)<0.01 && !card.querySelector('.library-card-body')})()") == "true", "collapsed portrait card contains no diagnostic body");
            Check(await Script("document.querySelector(" + J(card + " [data-windows-action=launch]") + ").getAttribute('aria-label').startsWith('Play')") == "true", "card surface is the primary launch action");
            await Script("Object.defineProperty(document,'hasFocus',{configurable:true,value:()=>true})");
            await Click(card + " .library-more-button");
            Check(Call("log", new { id = first.Id, path = "settings.json" }).Status == 400 && Call("log", new { id = "missing" }).Status == 409, "log API only accepts a retained library ID and no filesystem paths");
            await Script("document.querySelector(" + J(management + " [data-windows-action=viewLog]") + ").focus()");
            await Click(management + " [data-windows-action=viewLog]"); await Wait("!!document.querySelector('.windows-launch-log') && document.querySelector('.windows-launch-log').textContent.includes('Exit code: 37')");
            Check(await Script("!document.querySelector('.windows-launch-log script') && document.querySelector('.windows-launch-log').textContent.includes('<script>unsafe()</script>')") == "true", "saved crash output displays as selectable text without executing markup");
            File.AppendAllText(Path.Combine(logDirectory, first.LastLaunchLogId + ".log"), "\nREFRESHED_OUTPUT");
            await Script("[...document.querySelectorAll('[role=dialog] button')].find(b=>b.textContent==='Refresh log').click()");
            await Wait("document.querySelector('.windows-launch-log').textContent.includes('REFRESHED_OUTPUT')");
            var logEvidence = Environment.GetEnvironmentVariable("PS5_ORBIT_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(logEvidence)) { Directory.CreateDirectory(logEvidence); using var capture = File.Create(Path.Combine(logEvidence, "launch-log-viewer.png")); await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture); }
            await Script("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))"); await Wait("!document.querySelector('.windows-launch-log')");
            await Script("document.querySelector('[role=dialog] .close').click()"); await Wait("!document.querySelector('[role=dialog]')");
            await Wait("document.activeElement?.classList.contains('library-more-button')");
            await Click(card + " [data-windows-action=launch]"); await Task.Delay(100);
            Check(events.Single().Action == "launch" && events.Single().Id == first.Id, "Play dispatches original launcher action"); events.Clear();
            Check(await Script("!document.querySelector('[data-windows-action=openFolder]')") == "true", "secondary actions are absent from collapsed grid");
            await Click(card + " .library-more-button"); await Click(management + " [data-windows-action=openFolder]");
            Check(events.Single().Action == "openFolder" && events.Single().Id == first.Id, "More dispatches original native action and exact item without launching"); events.Clear();
            await Click(management + " [data-windows-action=compatibility]"); await Wait("!!document.querySelector('[role=dialog] select')");
            await Script("const select=document.querySelector('[role=dialog] select');select.value='in-game';select.dispatchEvent(new Event('change',{bubbles:true}))");
            await Script("[...document.querySelectorAll('[role=dialog] button')].find(b=>b.textContent==='Save compatibility').click()");
            await Wait("!document.querySelector('[role=dialog] select') && document.querySelector(" + J(card) + ").textContent.includes('In game')");
            Check(store.Load().CompatibilityOverrides["PPSA03647"] == "in-game" && nativeApi.Catalog.Releases.Single().WindowsCompatibility == "in-game" && nativeApi.Catalog.Releases.Single().WindowsCompatibilityLocal, "override persists and updates matching Browse metadata with local provenance");
            Check(store.Load().CompatibilityTestVersions["PPSA03647"].Version == model.CurrentKytyVersion && nativeApi.Catalog.Releases.Single().WindowsCompatibilityVersionMatch == "current", "custom save persists the native current executable version");
            Check(await Script("(()=>{const card=document.querySelector(" + J(management) + ");return card.querySelectorAll('.library-compatibility-status .windows-compatibility').length===3 && card.textContent.includes('Your result') && card.textContent.includes('Website report · windows') && card.textContent.includes('Tested on current version')})()") == "true", "library reuses main-page badges and displays separate custom/website versions and OS");
            var recorded = model.LibraryTestVersion(first);
            var secondFolder = Path.Combine(root, "second-build"); Directory.CreateDirectory(secondFolder);
            var secondExecutable = Path.Combine(secondFolder, "kyty_emulator.exe"); File.WriteAllText(secondExecutable, "KytyPS5-2026-10-17-1234567\0");
            model.EmulatorPath = secondExecutable;
            Check(model.CurrentKytyVersion == "KytyPS5-2026-10-17-1234567" && model.LibraryTestVersion(first) == recorded && nativeApi.Catalog.Releases.Single().WindowsCompatibilityVersionMatch == "different", "path update refreshes current version without rewriting existing test history");
            await model.RefreshCompatibilityAsync(_ => Task.FromResult(new Dictionary<string, string> { ["PPSA03647"] = "logo" }));
            Check(model.LibraryCompatibility(first) == "in-game" && model.LibraryWebsiteCompatibility(first) == "logo", "website refresh keeps custom result and separate community result");
            store.BeforeSave = _ => throw new IOException("fixture save failure");
            try { model.SetLibraryCompatibility(first, "logo"); throw new Exception("Failed override save accepted"); }
            catch (IOException) { Check(model.LibraryCompatibility(first) == "in-game", "failed save rolls back compatibility"); }
            store.BeforeSave = null;
            Check(Call("compatibility", new { id = first.Id, status = "playable" }).Status == 400, "unknown compatibility values are rejected");
            model.SetLibraryCompatibility(first, ""); Check(nativeApi.Catalog.Releases.Single().WindowsCompatibility == "logo", "reset restores community status"); model.SetLibraryCompatibility(first, "in-game");
            model.SetLibraryCompatibility(disposable, "main-menu"); Check(store.Load().CompatibilityOverrides["library:" + disposable.Id] == "main-menu", "unidentified item supports local status without guessing a title ID");
            await Script("document.querySelector('[role=dialog] .close').click()"); await Wait("!document.querySelector('[role=dialog]')");
            await Click(deleteCard + " .library-more-button");
            var deleteManagement = "[data-library-management='" + disposable.Id + "']";
            await Click(deleteManagement + " [data-windows-action=deleteLibrary]"); await Task.Delay(150);
            Check(events.Count == 0 && Directory.Exists(disposable.ContentPath), "ordinary click cannot delete");
            // Keep the synthetic control test independent of other foreground desktop work.
            // Production still uses the real document focus check; blur cancellation is tested below.
            await Script("Object.defineProperty(document,'hasFocus',{configurable:true,value:()=>true})");
            await Key(deleteManagement, "keydown"); await Wait("Number(document.querySelector(" + J(deleteManagement + " .delete-dial circle:last-child") + ").getAttribute('stroke-dashoffset'))<1");
            await Key(deleteManagement, "keyup"); await Task.Delay(2200);
            Check(events.Count == 0 && Directory.Exists(disposable.ContentPath), "early release cancels hold and preserves all content");
            await Key(deleteManagement, "keydown"); await Task.Delay(200); await Script("window.dispatchEvent(new Event('blur'))"); await Task.Delay(2100);
            Check(events.Count == 0 && Directory.Exists(disposable.ContentPath), "window blur cancels active hold");
            // Synthetic DOM pointer events have no native pointer capture; isolate that browser precondition.
            await Script("(()=>{const button=document.querySelector(" + J(deleteManagement + " [data-windows-action=deleteLibrary]") + ");button.focus();button.setPointerCapture=()=>{};button.dispatchEvent(new PointerEvent('pointerdown',{button:0,isPrimary:true,pointerId:7,bubbles:true,cancelable:true}))})()");
            await Task.Delay(250); await Script("document.querySelector(" + J(deleteManagement + " [data-windows-action=deleteLibrary]") + ").dispatchEvent(new PointerEvent('pointercancel',{pointerId:7,bubbles:true}))"); await Task.Delay(2100);
            Check(events.Count == 0 && Directory.Exists(disposable.ContentPath), "pointer cancellation preserves files");
            var evidence = Environment.GetEnvironmentVariable("PS5_ORBIT_EVIDENCE");
            await Script("document.querySelector('[role=dialog]').scrollTop=0"); await Task.Delay(100);
            if (!string.IsNullOrWhiteSpace(evidence)) { Directory.CreateDirectory(evidence); using var capture = File.Create(Path.Combine(evidence, "library-cards.png")); await host.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture); }
            await Key(deleteManagement, "keydown");
            await Wait("!document.querySelector(" + J(deleteCard) + ")");
            Check(events.Single().Action == "deleteLibrary" && !Directory.Exists(disposable.ContentPath) && Directory.Exists(first.ContentPath), "full keyboard hold deletes only synthetic chosen item through existing deletion transaction and fixture recycler");
            Check(store.Load().Library.Count == 1, "completed deletion persists");
        }
        finally
        {
            window.Close(); host.Dispose(); model.Dispose();
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            { try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
            if (Directory.Exists(root)) throw new IOException("Library card fixture remains in use: " + root);
        }
        Console.WriteLine("PASS library cards: poster, secondary actions, local compatibility persistence/rollback, click/short hold safety, one-use native guard and confirmed synthetic deletion");
        string J(string text) => JsonSerializer.Serialize(text);
        Task<string> Script(string code) => host.Browser.ExecuteScriptAsync(code);
        async Task Click(string selector) { await Wait("!!document.querySelector(" + J(selector) + ")"); await Script("document.querySelector(" + J(selector) + ").click()"); }
        async Task Key(string selector, string type) { await Script("document.querySelector(" + J(selector + " [data-windows-action=deleteLibrary]") + ").focus()"); await Task.Delay(150);
            await Script("document.querySelector(" + J(selector + " [data-windows-action=deleteLibrary]") + ").dispatchEvent(new KeyboardEvent(" + J(type) + ",{key:'Enter',bubbles:true,cancelable:true}))"); }
        async Task Wait(string code)
        { var deadline = DateTime.UtcNow.AddSeconds(15); while (DateTime.UtcNow < deadline) { if (await Script(code) == "true") return; await Task.Delay(100); } throw new Exception("Library card condition timed out: " + code + " " + await Script("document.body.innerText.slice(-1500)")); }
    }
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
}
