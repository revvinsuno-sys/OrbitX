using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class BrandingStartupTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5Branding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = new CatalogSource { Id = "branding", Name = "Offline branding fixture", SourceType = CatalogSourceType.OrbitLocal,
            EncryptedFilePath = Path.Combine(root, "catalog.enc"), KeyFilePath = Path.Combine(root, "key.bin") };
        File.WriteAllBytes(source.EncryptedFilePath, LocalOrbitCatalogTests.Seal(LocalOrbitCatalogTests.Feed()));
        File.WriteAllBytes(source.KeyFilePath, LocalOrbitCatalogTests.Key);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Interface = "Orbit Store", Sources = [source], DownloadRoot = Path.Combine(root, "downloads") });
        MainWindow? window = null;
        try
        {
            foreach (var fullscreen in new[] { false, true, false })
            {
                window = new MainWindow(store); window.Show();
                var model = (MainViewModel)window.DataContext;
                await Wait(() => !model.StartupLoading && ((ContentControl)window.FindName("OrbitSurface")).Content is OrbitFrontendHost);
                var host = (OrbitFrontendHost)((ContentControl)window.FindName("OrbitSurface")).Content;
                await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Until("document.querySelector('.app') && !document.querySelector('.orbitx-startup')");
                Check(fullscreen ? window.WindowStyle == WindowStyle.None : window.WindowStyle != WindowStyle.None, "actual restarted window applies saved display mode");
                if (!fullscreen && !store.Load().StartupFullscreen && model.Entries.Count > 0)
                {
                    await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"no-preference\"}]}");
                    model.StartupLoading = true;
                    var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args) { if (args.IsSuccess) navigation.TrySetResult(); else navigation.TrySetException(new IOException(args.WebErrorStatus.ToString())); }
                    host.Browser.CoreWebView2.NavigationCompleted += Completed;
                    try
                    {
                        await Script("sessionStorage.removeItem('orbitx.splash.shown');localStorage.setItem('orbit.interface.v1',JSON.stringify({name:'store',motion:'full'}));location.reload()");
                        await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    }
                    finally { host.Browser.CoreWebView2.NavigationCompleted -= Completed; }
                    await Until("!!document.querySelector('.orbitx-startup')");
                    await Task.Delay(1050);
                    await Assert("!!document.querySelector('.orbitx-startup:not(.leaving)') && getComputedStyle(document.querySelector('.orbitx-startup .orbitx-logo')).opacity==='1' && !!document.querySelector('.app').closest('[inert]') && getComputedStyle(document.querySelector('.app')).opacity==='0'", "splash fades in and holds while native startup is pending, with app hidden and interaction blocked");
                    await Capture("startup-held.png");
                    await Script("document.documentElement.dataset.motion='reduced'");
                    Console.WriteLine("Reduced splash motion: " + await Script("JSON.stringify({motion:document.documentElement.dataset.motion,animation:getComputedStyle(document.querySelector('.orbitx-startup .orbitx-logo')).animationName,transition:getComputedStyle(document.querySelector('.orbitx-startup')).transitionDuration})"));
                    await Assert("getComputedStyle(document.querySelector('.orbitx-startup .orbitx-logo')).animationName==='none' && parseFloat(getComputedStyle(document.querySelector('.orbitx-startup')).transitionDuration)<.001", "reduced motion removes nonessential splash animation");
                    await Script("document.documentElement.dataset.motion='full'");
                    model.StartupLoading = false;
                    await Until("!!document.querySelector('.orbitx-startup.leaving')");
                    await Assert("document.querySelectorAll('.game-tile').length>0", "tiles populate before splash fades out");
                    await Task.Delay(120);
                    await Assert("(()=>{const splash=Number(getComputedStyle(document.querySelector('.orbitx-startup')).opacity),app=Number(getComputedStyle(document.querySelector('.app')).opacity);return splash>0 && splash<1 && app>0 && app<1 && Math.abs(splash+app-1)<.08})()", "loaded app and splash crossfade together");
                    await Capture("startup-fading.png");
                    await Until("!document.querySelector('.orbitx-startup')");
                    await Assert("!document.querySelector('.app').closest('[inert]') && getComputedStyle(document.querySelector('.app')).opacity==='1' && !document.querySelector('.app.startup-pending')", "splash restores interaction and full app opacity after dismissal");
                    await Capture("branded-home.png");
                }
                if (model.Entries.Count == 0)
                {
                    await Assert("!!document.querySelector('.empty') && !document.querySelector('.orbitx-startup')", "empty catalogue stays usable after splash dismissal");
                    await Capture("branded-empty.png");
                }
                Check(window.Icon is not null, "native window uses the supplied branding mark");
                await Script("[...document.querySelectorAll('.main-nav button')].find(button=>button.textContent.trim()==='Settings').click()");
                await Until("!!document.querySelector('[data-settings-category=appearance]')");
                await Script("document.querySelector('[data-settings-category=appearance]').click()");
                await Until("!!document.querySelector('.orbit-settings-content [role=combobox]')");
                await Script("document.querySelector('.orbit-settings-content [role=combobox]').focus();document.querySelector('.orbit-settings-content [role=combobox]').scrollIntoView({block:'center'});document.querySelector('.orbit-settings-content [role=combobox]').click()");
                await Until("!!document.querySelector('[role=option]')");
                await Script($"[...document.querySelectorAll('[role=option]')].find(option=>option.textContent.trim()==='{(fullscreen ? "Windowed" : "Fullscreen")}').click()");
                await Wait(() => store.Load().StartupFullscreen == !fullscreen);
                await Capture(fullscreen ? "startup-windowed-setting.png" : "startup-fullscreen-setting.png");
                // Verify an empty catalogue also dismisses the splash on the last launch.
                window.Close(); window = null;
                if (fullscreen) { var saved = store.Load(); saved.Sources[0].Enabled = false; store.Save(saved); }

                Task<string> Script(string script) => host.Browser.ExecuteScriptAsync(script);
                async Task Until(string expression) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (await Script(expression) != "true") await Task.Delay(25, timeout.Token); }
                async Task Assert(string expression, string message) => Check(await Script(expression) == "true", message);
                async Task Capture(string name)
                {
                    if (Environment.GetEnvironmentVariable("PS5_SETTINGS_EVIDENCE") is not { Length: > 0 } evidence) return;
                    Directory.CreateDirectory(evidence);
                    using var capture = JsonDocument.Parse(await host.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"captureBeyondViewport\":false}").WaitAsync(TimeSpan.FromSeconds(20)));
                    await File.WriteAllBytesAsync(Path.Combine(evidence, name), Convert.FromBase64String(capture.RootElement.GetProperty("data").GetString()!));
                }
            }
            Console.WriteLine("PASS branding/startup: supplied logo, readiness-gated fade, populated and empty catalogue, inert restoration, saved graphical windowed/fullscreen launches");
        }
        finally
        {
            window?.Close();
            for (var attempt = 0; attempt < 60 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
            if (Directory.Exists(root)) throw new IOException("Branding fixture remains in use: " + root);
        }
    }
    private static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!condition()) await Task.Delay(50, timeout.Token); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
