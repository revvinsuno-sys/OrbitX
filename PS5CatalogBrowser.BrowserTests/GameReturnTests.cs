using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class GameReturnTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5GameReturn-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(AppContext.BaseDirectory, "kyty_emulator.exe");
        if (File.Exists(worker)) throw new IOException("Refusing to replace an existing worker executable.");
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        try
        {
            CheckBuildIdentity(root);
            File.Copy(Environment.ProcessPath!, worker);
            var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
            var items = new List<LocalLibraryEntry>();
            foreach (var code in new byte[] { 0, 17 })
            {
                var game = Path.Combine(installed, $"controller-return-{code}.elf");
                File.WriteAllBytes(game, [0x7f, (byte)'E', (byte)'L', (byte)'F', code]);
                items.Add(new() { Title = $"Return fixture {code}", ContentPath = game, Classification = "Direct ELF launch attempt" });
            }
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = installed,
                Interface = "Classic Windows", EmulatorPath = worker, Library = items });
            window = new MainWindow(store);
            window.Show();
            var model = (MainViewModel)window.DataContext;
            var tabs = (TabControl)window.FindName("ClassicTabs");
            foreach (var item in model.Library.ToArray())
            {
                OrbitFrontendHost? orbit = null;
                if (item.Title.EndsWith("17", StringComparison.Ordinal))
                {
                    model.Interface = "Orbit Store";
                    var surface = (ContentControl)window.FindName("OrbitSurface");
                    await WaitAsync(() => Task.FromResult(surface.Content is OrbitFrontendHost));
                    orbit = (OrbitFrontendHost)surface.Content;
                    await orbit.InitializeAsync(model);
                    await WaitAsync(async () => await orbit.Browser.ExecuteScriptAsync("document.readyState === 'complete' && document.querySelector('#root').childElementCount > 0") == "true");
                }
                tabs.SelectedIndex = 0;
                window.WindowState = WindowState.Minimized;
                var returned = new TaskCompletionSource<LocalLibraryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnReturn(LocalLibraryEntry value) => returned.TrySetResult(value);
                model.GameExited += OnReturn;
                try
                {
                    model.Launch(item);
                    var actual = await returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Check(ReferenceEquals(actual, item) && ReferenceEquals(model.SelectedLibraryEntry, item), "Return must preserve the launched library entry.");
                    Check(window.IsVisible && window.WindowState != WindowState.Minimized, "Exit must restore the browser window.");
                    if (orbit is null)
                        Check(((TabItem)tabs.SelectedItem).Header.ToString() == "My Library", "Exit must select Classic My Library.");
                    else
                    {
                        var selector = JsonSerializer.Serialize($".windows-library-card[data-windows-id='{item.Id}']");
                        await WaitAsync(async () => await orbit.Browser.ExecuteScriptAsync($"!!document.querySelector({selector})") == "true");
                        Check(((ContentControl)window.FindName("OrbitSurface")).IsVisible && model.Interface == "Orbit Store", "Return must keep Orbit visible and navigate its real Library page.");
                    }
                    var log = KytyLaunchLog.Read(Path.Combine(store.DirectoryPath, "launch-logs"), item.LastLaunchLogId);
                    Check(log.Contains("Synthetic controller return worker") && log.Contains("Process exited"), "Return must wait for saved process exit output.");
                    Check(model.Status.Contains(item.Title) && (item.Title.EndsWith('0') ? model.Status.StartsWith("Returned to My Library") : model.Status.Contains("code 17")), "Normal and failed exits must have accurate status.");
                }
                finally { model.GameExited -= OnReturn; }
            }
            window.Close(); window = null;
            Check(store.Load().Library.All(item => item.LastLaunchLogId.Length == 32 && File.Exists(item.ContentPath)), "Launch logs must persist and game files must survive.");
            Console.WriteLine("PASS game return: real child exits 0/17, Classic and Orbit library restoration, selected entry, saved logs, preserved content");
        }
        finally
        {
            window?.Close();
            File.Delete(worker);
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fullRoot).StartsWith("PS5GameReturn-", StringComparison.Ordinal))
            {
                for (var attempt = 0; attempt < 100 && Directory.Exists(fullRoot); attempt++)
                {
                    try { Directory.Delete(fullRoot, true); }
                    catch (IOException) { await Task.Delay(100); }
                    catch (UnauthorizedAccessException) { await Task.Delay(100); }
                }
                if (Directory.Exists(fullRoot)) throw new IOException("WebView2 has not released the synthetic test profile: " + fullRoot);
            }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void CheckBuildIdentity(string root)
    {
        const string tag = "KytyPS5-2026-10-13-9e41d83";
        const string current = tag + "-120-g415387f0-dirty";
        var directory = Path.Combine(root, "version"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kyty_emulator.exe");
        foreach (var version in new[] { tag, current })
            for (var boundary = 0; boundary <= version.Length; boundary++)
            {
                var encoded = Encoding.ASCII.GetBytes(version);
                var bytes = new byte[65536 - boundary + encoded.Length + 1];
                encoded.CopyTo(bytes, 65536 - boundary);
                File.WriteAllBytes(path, bytes);
                Check(KytyVersion.Read(path) == version, "Build identity must survive every read-buffer boundary without truncation.");
            }
        Check(KytyVersion.Comparison(current, current, "in-game") == "current" &&
            KytyVersion.Comparison(tag, current, "in-game") == "different" &&
            KytyVersion.Comparison(current.Replace("415387f0", "abcdef01"), current, "in-game") == "different",
            "Main commits and custom dirty builds must not compare equal to their last tag.");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(tag + "\0" + current + "\0"));
        Check(KytyVersion.Read(path).Length == 0, "Distinct embedded builds must remain ambiguous.");
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }
}
