using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class KytyUpdateUiTests
{
    public static async Task RunAsync()
    {
        const string old = "KytyPS5-2026-10-13-9e41d83", latest = "KytyPS5-2026-10-18-099de8d";
        Check(KytyVersion.MatchesRelease(latest + "-dirty", latest) && KytyVersion.MatchesRelease(latest, latest), "menu patch suffix is the same upstream release");
        Check(!KytyVersion.MatchesRelease(latest + "-1-g1234567-dirty", latest) && !KytyVersion.MatchesRelease(old, latest) && !KytyVersion.MatchesRelease(latest + "a", latest), "other source revisions are different, including same-date builds");
        var root = Path.Combine(Path.GetTempPath(), "PS5KytyUpdate-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "Tools", "KytyPS5", "kyty_emulator.exe"); Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
        File.WriteAllText(runtime, old + "-dirty\0");
        var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(new() { Sources = [], EmulatorPath = runtime });
        MainWindow? window = null;
        try
        {
            window = new MainWindow(store); var model = (MainViewModel)window.DataContext;
            var calls = 0;
            model.LookupKytyRelease = () => Task.FromResult(latest);
            var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            model.RunKytyUpdater = (start, output) =>
            {
                calls++;
                Check(start.FileName == "powershell.exe" && !start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardOutput && start.RedirectStandardError, "hidden direct updater process");
                Check(start.ArgumentList.Contains("-Force") && start.ArgumentList[start.ArgumentList.IndexOf("-RuntimePath") + 1] == runtime && start.WorkingDirectory == Path.GetDirectoryName(runtime), "manual updater receives the selected runtime and rebuild flag through ArgumentList");
                output("KytyPS5: Compiling the patched Release build..."); output(new string('x', 70000));
                return done.Task;
            };
            window.Show();
            await Wait(() => !model.IsCheckingKytyVersion && model.RepositoryKytyVersion == latest);
            var tabs = (TabControl)window.FindName("SettingsTabs"); ((TabItem)window.FindName("ClassicSettingsTab")).IsSelected = true;
            tabs.Items.Cast<TabItem>().Single(tab => (string)tab.Header == "KytyPS5").IsSelected = true; window.UpdateLayout();
            var update = (Button)window.FindName("UpdateKytyButton");
            Check(calls == 0 && model.CurrentKytyVersion == old + "-dirty" && update.IsEnabled, "startup only checks; differing versions enable the actual Update control");
            if (Environment.GetEnvironmentVariable("PS5_KYTY_UPDATE_EVIDENCE") is { Length: > 0 } evidence)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(evidence); png.Save(file);
            }
            update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => model.IsUpdatingKyty);
            Check(!update.IsEnabled && !model.CanCheckKytyVersion && !model.CanEditKytyPath && calls == 1, "update state prevents concurrent updates, checks and path changes");
            await model.UpdateKytyAsync(); Check(calls == 1, "double update cannot start a second process");
            try { model.EmulatorPath = "changed"; throw new Exception("Path changed during update"); } catch (InvalidOperationException) { }
            try { model.Launch(new()); throw new Exception("Game launched during update"); } catch (IOException ex) { Check(ex.Message.Contains("update"), "game launch reports update in progress"); }
            window.Close(); Check(window.IsVisible, "normal close waits for the updater to finish");
            File.WriteAllText(runtime, latest + "-dirty\0"); done.SetResult(0);
            await Wait(() => !model.IsUpdatingKyty);
            Check(model.CurrentKytyVersion == latest + "-dirty" && !model.CanUpdateKyty && model.KytyUpdateStatus.Contains("updated") && model.KytyUpdateOutput.Length <= 65536, "successful update refreshes installed identity and bounds output");
            Check(((TextBlock)window.FindName("InstalledKytyVersionText")).Text == latest + "-dirty" && ((TextBlock)window.FindName("RepositoryKytyVersionText")).Text == latest, "bound installed and repository versions refresh");
            File.WriteAllText(runtime, old + "-dirty\0");
            await model.CheckForKytyUpdateAsync();
            model.RunKytyUpdater = (_, output) => { calls++; output("Fixture patch conflict: no runtime files replaced."); return Task.FromResult(1); };
            await model.UpdateKytyAsync();
            Check(model.CanUpdateKyty && model.KytyUpdateStatus.Contains("failed") && model.KytyUpdateOutput.Contains("patch conflict") && File.ReadAllText(runtime) == old + "-dirty\0", "failure retains installed runtime, reports diagnostic and enables retry");
            model.LookupKytyRelease = () => throw new HttpRequestException("Fixture offline");
            await model.CheckForKytyUpdateAsync();
            Check(!model.CanUpdateKyty && model.CanCheckKytyVersion && model.RepositoryKytyVersion == "Not checked" && model.KytyUpdateStatus.Contains("offline"), "offline check clears stale availability and permits retry");
            model.LookupKytyRelease = () => Task.FromResult("invalid-tag"); await model.CheckForKytyUpdateAsync();
            Check(!model.CanUpdateKyty && model.KytyUpdateStatus.Contains("unrecognized"), "malformed repository version does not enable update");
            model.LookupKytyRelease = () => Task.FromResult(latest); await model.CheckForKytyUpdateAsync();
            var external = Path.Combine(root, "external", "kyty_emulator.exe"); Directory.CreateDirectory(Path.GetDirectoryName(external)!); File.WriteAllText(external, old + "\0");
            model.EmulatorPath = external;
            Check(!model.CanUpdateKyty && model.KytyUpdateStatus.Contains("managed"), "unmanaged executable cannot invoke the managed installer and receives an explanation");
            Check(store.Load().Downloads.Count == 0 && store.Load().Library.Count == 0 && store.Load().CompatibilityTestVersions.Count == 0, "updater UI does not rewrite download, library or compatibility records");
            window.Close(); window = null;
            Console.WriteLine("PASS Kyty update UI: installed/repo versions, read-only startup, real Update button, direct process arguments, progress, bounded log, concurrency, close/launch guards, success, failure, retry and managed path");
        }
        finally { window?.Close(); Directory.Delete(root, true); }
    }
    private static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!condition()) await Task.Delay(25, timeout.Token); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
