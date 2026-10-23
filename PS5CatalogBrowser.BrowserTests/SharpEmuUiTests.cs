using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class SharpEmuUiTests
{
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("PS5_SETTINGS_EVIDENCE") is not { Length: > 0 } directory) return;
        window.UpdateLayout(); Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmu-UI-" + Guid.NewGuid().ToString("N"));
        var sharp = Path.Combine(AppContext.BaseDirectory, "SharpEmu.exe");
        var kyty = Path.Combine(AppContext.BaseDirectory, "kyty_emulator.exe");
        if (File.Exists(sharp) || File.Exists(kyty)) throw new IOException("Synthetic worker path already exists.");
        Directory.CreateDirectory(root); MainWindow? window = null;
        try
        {
            File.Copy(Environment.ProcessPath!, sharp); File.Copy(Environment.ProcessPath!, kyty);
            var games = Path.Combine(root, "installed");
            var folder = Path.Combine(games, "dual-fixture-game with spaces"); Directory.CreateDirectory(folder);
            var bytes = new byte[128]; bytes[0] = 0x7f; bytes[1] = 0x45; bytes[2] = 0x4c; bytes[3] = 0x46;
            bytes[4] = 2; bytes[5] = 1; bytes[6] = 1; bytes[7] = 9;
            void U16(int offset, ushort value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
            void U64(int offset, ulong value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
            U16(16, 0xfe10); U16(18, 62); bytes[20] = 1; U64(32, 64); U16(52, 64); U16(54, 56); U16(56, 1);
            bytes[64] = 1; U64(72, 120); U64(96, 8); U64(104, 8);
            File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), bytes);
            var item = new LocalLibraryEntry { Title = "Dual emulator fixture", ContentPath = folder, Classification = "Ready for launch attempt" };
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Sources = [], InstalledGamesRoot = games, EmulatorPath = kyty, SharpEmuPath = sharp, Library = [item], IgdbClientId = "offline-fixture" });
            window = new MainWindow(store);
            var model = (MainViewModel)window.DataContext; item = model.Library.Single(); model.SelectedLibraryEntry = item;
            model.LookupKytyRelease = () => Task.FromResult("KytyPS5-2026-10-18-099de8d");
            window.Show(); await model.RefreshFolderActionsAsync(item);
            Check(item.LastEmulator == EmulatorBackend.KytyPS5, "never launched defaults to Kyty");
            async Task Launch(EmulatorBackend? backend)
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Done(LocalLibraryEntry _) => exited.TrySetResult();
                model.GameExited += Done;
                try
                {
                    model.Launch(item, emulator: backend);
                    await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var log = model.ReadLaunchLog(item);
                    Check(log.Contains("DUAL_ARGV="), "real selected child emitted arguments");
                    if ((backend ?? item.LastEmulator) == EmulatorBackend.SharpEmu)
                        Check(log.Contains("--log-file") && log.Contains("eboot.bin") && !log.Contains("--game"), "SharpEmu child receives source-verified argument vector");
                }
                finally { model.GameExited -= Done; }
            }
            await Launch(null);
            await Launch(EmulatorBackend.SharpEmu);
            Check(store.Load().Library.Single().LastEmulator == EmulatorBackend.SharpEmu, "successful SharpEmu process persists choice");
            await Launch(null);
            Check(new SettingsStore(store.DirectoryPath).Load().Library.Single().LastEmulator == EmulatorBackend.SharpEmu, "restart uses saved backend");
            await Launch(EmulatorBackend.KytyPS5);
            Check(store.Load().Library.Single().LastEmulator == EmulatorBackend.KytyPS5, "switch back persists");
            // A real Process.Start failure must not change the last successful backend.
            FileStream? executableWriter = null;
            store.BeforeSave = _ => executableWriter ??= new FileStream(sharp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            try { model.Launch(item, emulator: EmulatorBackend.SharpEmu); throw new Exception("Write-locked process started"); }
            catch (System.ComponentModel.Win32Exception) { }
            finally { store.BeforeSave = null; executableWriter?.Dispose(); }
            Check(item.LastEmulator == EmulatorBackend.KytyPS5 && store.Load().Library.Single().LastEmulator == EmulatorBackend.KytyPS5, "failed process start preserves preference");
            // Exercise the actual native dialog and cancellation handler, without launching.
            var before = item.LastLaunchLogId;
            _ = window.Dispatcher.BeginInvoke(() => Application.Current.Windows.OfType<EmulatorSelectionDialog>().Single().Close());
            var choose = window.GetType().GetMethod("ChooseGameEmulator_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            choose.Invoke(window, [window, new RoutedEventArgs()]);
            Check(item.LastLaunchLogId == before && store.Load().Library.Single().LastEmulator == EmulatorBackend.KytyPS5, "native selector cancellation does not launch or mutate");
            var chosenExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ChosenExit(LocalLibraryEntry _) => chosenExit.TrySetResult();
            model.GameExited += ChosenExit;
            try
            {
                _ = window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
                {
                    var dialog = Application.Current.Windows.OfType<EmulatorSelectionDialog>().Single();
                    Capture(dialog, "emulator-selector.png");
                    ((StackPanel)dialog.Content).Children.OfType<Button>().Single(button => button.Content as string == "SharpEmu")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                choose.Invoke(window, [window, new RoutedEventArgs()]);
                await chosenExit.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Check(store.Load().Library.Single().LastEmulator == EmulatorBackend.SharpEmu && model.ReadLaunchLog(item).Contains("--log-file"), "native selector choice launches SharpEmu and persists after start");
            }
            finally { model.GameExited -= ChosenExit; }
            var tabs = (TabControl)window.FindName("SettingsTabs");
            ((TabItem)window.FindName("ClassicSettingsTab")).IsSelected = true;
            tabs.Items.Cast<TabItem>().Single(tab => tab.Header as string == "Emulators").IsSelected = true;
            window.UpdateLayout();
            Check(((TextBox)window.FindName("SharpEmuPathBox")).Text == sharp && model.SharpEmuOwnership.Contains("External"), "Settings binds independent external path");
            // Real WebView cover handlers; synthetic capture mirrors the browser-owned pointer capture.
            using var host = new OrbitFrontendHost();
            var webWindow = new Window { Content = host, Width = 900, Height = 700, Owner = window };
            var actions = new List<OrbitFrontendAction>(); host.ActionRequested += actions.Add;
            try
            {
                webWindow.Show(); await host.InitializeAsync(model); host.NavigatePage("Library");
                async Task<string> Script(string script) => await host.Browser.ExecuteScriptAsync(script);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await Script("!!document.querySelector('.library-play-surface')") != "true") await Task.Delay(30, timeout.Token);
                await Script("window.cover=document.querySelector('.library-play-surface');cover.setPointerCapture=()=>{};cover.hasPointerCapture=()=>false;cover.releasePointerCapture=()=>{};window.ptr=(type,x=20)=>cover.dispatchEvent(new PointerEvent(type,{bubbles:true,cancelable:true,pointerId:1,isPrimary:true,button:0,clientX:x,clientY:20}));window.clickCover=()=>cover.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,detail:1}));");
                await Script("ptr('pointerdown');ptr('pointerup');clickCover()"); await Task.Delay(100);
                Check(actions.Count == 1 && actions[0].Action == "launch", "short click launches once without picker"); actions.Clear();
                await Script("ptr('pointerdown')"); await Task.Delay(750);
                await Script("ptr('pointerup');clickCover()"); await Task.Delay(100);
                Check(actions.Count == 1 && actions[0].Action == "chooseEmulator", "long press selects once and suppresses normal click"); actions.Clear();
                foreach (var cancellation in new[] { "ptr('pointermove',80)", "ptr('pointerout')", "ptr('pointercancel')", "ptr('lostpointercapture')", "window.dispatchEvent(new Event('blur'))" })
                {
                    await Script("ptr('pointerdown');" + cancellation); await Task.Delay(700);
                    await Script("ptr('pointerup');clickCover()"); await Task.Delay(40);
                    Check(actions.Count == 0, "pointer cancellation prevents launch: " + cancellation);
                }
                await Script("cover.click()"); await Task.Delay(80);
                Check(actions.Count == 1 && actions[0].Action == "launch", "keyboard/controller activation remains normal launch"); actions.Clear();
                await Script("cover.click();cover.click()"); await Task.Delay(80);
                Check(actions.Count == 1, "rapid activation does not send duplicate launch requests"); actions.Clear();
                await Script("document.querySelector('.library-more-button').click()");
                while (await Script("!!document.querySelector('[role=dialog] [data-windows-action=chooseEmulator]')") != "true") await Task.Delay(30, timeout.Token);
                await Script("document.querySelector('[role=dialog] [data-windows-action=chooseEmulator]').click()"); await Task.Delay(80);
                Check(actions.Count == 1 && actions[0].Action == "chooseEmulator", "More exposes the keyboard-accessible emulator selector");
            }
            finally { webWindow.Close(); }
            window.Close(); window = null;
            Check(new SettingsStore(store.DirectoryPath).Load().SharpEmuPath == sharp, "external executable selection survives restart");
            Console.WriteLine("PASS real dual-emulator processes/preferences, failed startup, native selector cancel/settings and WebView short/long/cancelled pointer gestures");
        }
        finally
        {
            window?.Close(); File.Delete(sharp); File.Delete(kyty);
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            { try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
        }
    }
}
