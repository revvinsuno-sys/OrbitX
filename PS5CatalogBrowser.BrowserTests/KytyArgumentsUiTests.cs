using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class KytyArgumentsUiTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5ArgumentUi-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(AppContext.BaseDirectory, "kyty_emulator.exe");
        if (File.Exists(worker)) throw new IOException("Refusing to replace an existing worker.");
        Directory.CreateDirectory(root); MainWindow? window = null;
        try
        {
            File.Copy(Environment.ProcessPath!, worker);
            var installed = Path.Combine(root, "installed games"); Directory.CreateDirectory(installed);
            var game = Path.Combine(installed, "argument-fixture-game with spaces.elf");
            File.WriteAllBytes(game, [0x7f, (byte)'E', (byte)'L', (byte)'F', 0]);
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var item = new LocalLibraryEntry { Title = "Argument fixture", ContentPath = game, Classification = "Direct ELF launch attempt" };
            store.Save(new() { Sources = [], EmulatorPath = worker, InstalledGamesRoot = installed, Library = [item] });
            window = new MainWindow(store); window.Show();
            var model = (MainViewModel)window.DataContext; item = model.Library.Single(); model.SelectedLibraryEntry = item;
            Check(!model.PerformanceArgumentsEnabled && model.ArgumentsPreview == "", "legacy WPF settings retain effective defaults");
            model.PerformanceArgumentsEnabled = true;
            var widthBox = (TextBox)window.FindName("ArgumentWidthBox");
            widthBox.SetCurrentValue(TextBox.TextProperty, "1366");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            model.ArgumentHeight = "768"; model.AdditionalArguments = "--user-name \"Player One\"";
            model.ArgumentAmdCpuPatches = true; model.ArgumentRedZoneProtection = true;
            model.ArgumentShaderCacheFolder = Path.Combine(root, "shader cache with spaces");
            Check(model.ArgumentsPreview.Contains("--screen-width 1366") && model.ArgumentsPreview.Contains("\"Player One\""), "preview follows WPF edits");
            model.ArgumentVblank = "0";
            Check(model.ArgumentsValidation.Contains("Vblank frequency"), "inline actionable numeric error");
            try { model.ApplyArguments(); throw new Exception("Invalid draft saved"); } catch (ArgumentException) { }
            Check(store.Load().KytyArguments is null, "invalid draft cannot replace saved settings");
            model.ArgumentVblank = "30"; model.AdditionalArguments = "--game other";
            Check(model.ArgumentsValidation.Contains("--game"), "conflicting game is shown inline");
            model.AdditionalArguments = "--user-name \"Player One\"";
            var tabs = (TabControl)window.FindName("ClassicTabs");
            tabs.Items.Cast<TabItem>().Single(tab => tab.Header as string == "Settings").IsSelected = true;
            ((TabControl)window.FindName("SettingsTabs")).Items.Cast<TabItem>().Single(tab => tab.Header as string == "KytyPS5").IsSelected = true;
            window.UpdateLayout();
            ((Button)window.FindName("ApplyArgumentsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Load().KytyArguments?.ScreenWidth == 1366 && model.ArgumentsSaveState.StartsWith("Saved"), "Apply saves custom resolution via real Settings button");
            if (Environment.GetEnvironmentVariable("PS5_ARGUMENTS_EVIDENCE") is { Length: > 0 } evidence)
            {
                ((Button)window.FindName("ApplyArgumentsButton")).BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(evidence); encoder.Save(output);
            }
            tabs.Items.Cast<TabItem>().Single(tab => tab.Header as string == "My Library").IsSelected = true;
            await CaptureLaunch(() => ((Button)window.FindName("LaunchLibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), 30, 0);
            model.ArgumentVblank = "60"; model.ArgumentPresentMode = "Immediate"; model.ArgumentShaderOptimization = "None";
            model.ArgumentPerformanceOverlay = false; model.AdditionalArguments += " --no-splash"; model.ApplyArguments();
            model.Interface = "Orbit Store";
            var surface = (ContentControl)window.FindName("OrbitSurface");
            await Wait(() => Task.FromResult(surface.Content is OrbitFrontendHost));
            var host = (OrbitFrontendHost)surface.Content!; await host.InitializeAsync(model); host.NavigatePage("Library");
            var card = $"[data-windows-id='{item.Id}']";
            await Wait(async () => await host.Browser.ExecuteScriptAsync($"!!document.querySelector({JsonSerializer.Serialize(card + " [data-windows-action=launch]")})") == "true");
            var previousLog = item.LastLaunchLogId;
            await host.Browser.ExecuteScriptAsync($"document.querySelector({JsonSerializer.Serialize(card + " .library-more-button")}).click()");
            await Task.Delay(150);
            Check(item.LastLaunchLogId == previousLog, "secondary More control does not launch a game");
            await host.Browser.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            await CaptureLaunch(() => _ = host.Browser.ExecuteScriptAsync($"document.querySelector({JsonSerializer.Serialize(card + " [data-windows-action=launch]")}).click()"), 60, 17);
            await host.Browser.ExecuteScriptAsync("window.chrome.webview.postMessage({version:1,command:'navigate',page:'Settings'})");
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!!document.querySelector('.orbit-settings')") == "true");
            await CaptureLaunch(() => model.Launch(item), 60, 17);
            await Wait(async () => await host.Browser.ExecuteScriptAsync("!document.querySelector('.orbit-settings') && !!document.querySelector('[aria-label=\"My Library\"]')") == "true");
            Check(surface.IsVisible, "game exit while Settings is open restores graphical My Library");
            var saved = store.Load();
            Check(saved.KytyArguments!.VblankFrequency == 60 && saved.KytyArguments.AdditionalArguments.EndsWith("--no-splash"), "failed launch retains settings for correction");
            Check(saved.Downloads.Count == 0 && saved.Library.Count == 1 && saved.CompatibilityOverrides.Count == 0 && saved.CompatibilityTestVersions.Count == 0 && File.Exists(game), "launch does not alter content, downloads or compatibility records");
            window.Close(); window = null;
            using var restarted = new MainViewModel(store);
            Check(restarted.ArgumentWidth == "1366" && restarted.ArgumentHeight == "768" && restarted.ArgumentVblank == "60" && restarted.ArgumentPresentMode == "Immediate" && !restarted.ArgumentPerformanceOverlay && restarted.AdditionalArguments.EndsWith("--no-splash"), "restart restores all saved controls");
            Check(restarted.ArgumentAmdCpuPatches && restarted.ArgumentRedZoneProtection && restarted.ArgumentShaderCacheFolder == Path.Combine(root, "shader cache with spaces") && !Directory.Exists(restarted.ArgumentShaderCacheFolder), "restart restores CPU/cache controls without creating or moving caches");
            Console.WriteLine("PASS Kyty arguments UI: settings edits, preview, validation, persistence, real Classic/Orbit child argv, spaces, next launch, failed exit and secondary controls");

            async Task CaptureLaunch(Action action, int vblank, int exit)
            {
                var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnExit(LocalLibraryEntry _) => returned.TrySetResult();
                model.GameExited += OnExit;
                try { action(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
                finally { model.GameExited -= OnExit; }
                var log = model.ReadLaunchLog(item);
                const string marker = "ACTUAL_ARGV=";
                var start = log.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                Check(start >= marker.Length, "real child emitted argv");
                var actual = JsonSerializer.Deserialize<string[]>(log[start..].Split('\n')[0].Replace("[stdout] ", "").Trim())!;
                Check(actual.SequenceEqual(new[] { "--game", game }.Concat(KytyArguments.Build(model.Settings.KytyArguments))), "actual process argv matches authoritative saved options: actual=" + JsonSerializer.Serialize(actual) + "; expected=" + JsonSerializer.Serialize(new[] { "--game", game }.Concat(KytyArguments.Build(model.Settings.KytyArguments))));
                Check(actual.Count(arg => arg == "--game") == 1 && actual[1] == game && actual[Array.IndexOf(actual, "--vblank-frequency") + 1] == vblank.ToString(), "one generated game path and next-launch vblank");
                Check(actual.Count(arg => arg == "--amd-cpu") == 1 && actual.Count(arg => arg == "--redzone") == 1 && actual[Array.IndexOf(actual, "--shader-cache-folder") + 1] == Path.Combine(root, "shader cache with spaces"), "real Classic/Orbit processes receive CPU switches and the complete cache folder argument");
                Check(KytyArguments.Preview(actual.Skip(2)) == model.ArgumentsPreview && log.Contains($"Exit code: {exit} "), "preview equals actual argv and exit retained");
                if (exit != 0) Check(model.Status.Contains("code 17"), "launch failure is reported");
            }
        }
        finally
        {
            window?.Close(); File.Delete(worker);
            for (var attempt = 0; attempt < 60 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
            if (Directory.Exists(root)) throw new IOException("Argument UI fixture still in use: " + root);
        }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!await condition()) await Task.Delay(50, timeout.Token); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
