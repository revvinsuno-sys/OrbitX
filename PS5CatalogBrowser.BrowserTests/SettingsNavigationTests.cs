using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class SettingsNavigationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5SettingsTabs-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [new CatalogSource { Name = "Disabled fixture catalogue", Url = "https://example.invalid/" + new string('x', 180), Enabled = false }], DownloadRoot = Path.Combine(root, "downloads"), ArchivePasswords = ["first fixture", "second fixture", "third fixture"], IgdbClientId = "offline-fixture" });
        MainWindow? window = null;
        try
        {
            window = new MainWindow(store); window.Show();
            var model = (MainViewModel)window.DataContext;
            var categories = (TabControl)window.FindName("SettingsTabs");
            var classicTab = (TabItem)window.FindName("ClassicSettingsTab");
            classicTab.IsSelected = true;
            var names = categories.Items.Cast<TabItem>().Select(tab => (string)tab.Header).ToArray();
            Check(names.SequenceEqual(new[] { "Catalogue sources", "Downloads", "Archive passwords", "Embedded browser", "Image installation", "SharpEmu", "KytyPS5", "About OrbitX" }), "all existing categories remain available");
            var workspace = (Grid)window.FindName("SettingsWorkspace");
            var tiles = ((WrapPanel)window.FindName("SettingsTiles")).Children.Cast<Button>().ToArray();
            window.UpdateLayout();
            Check(tiles.Length == 8 && categories.Visibility == Visibility.Collapsed, "settings opens on the seven-category hub with integrated About navigation");
            await Capture("settings-home-classic.png");
            tiles[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Check(categories.IsVisible && categories.SelectedIndex == 0, "first category opens even when already selected internally");
            tiles[0].Focus();
            var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            tiles[0].RaiseEvent(right);
            Check(right.Handled && tiles[1].IsKeyboardFocused && categories.SelectedIndex == 0, "arrow keys move tile focus without invoking an operation");
            tiles[1].Focus();
            tiles[1].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(categories.SelectedIndex == 1, "Enter activates a focused native category button");
            Select("Downloads");
            var download = (TextBox)window.FindName("DownloadRootBox");
            Check(download.IsVisible, "Downloads tab shows existing controls");
            download.SetCurrentValue(TextBox.TextProperty, Path.Combine(root, "changed downloads"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(store.Load().DownloadRoot == download.Text, "existing immediate settings persistence is preserved");
            var bandwidth = (TextBox)window.FindName("DownloadBandwidthBox"); bandwidth.Focus(); bandwidth.Text = "75"; tiles[2].Focus();
            Check(store.Load().DownloadBandwidthMBps == 75, "bandwidth edits retain LostFocus persistence");
            Select("Archive passwords");
            var passwords = (MaskedPasswordList)window.FindName("ArchivePasswordsBox");
            Check(passwords.IsVisible && Descendants<PasswordBox>(passwords).Any() && !Descendants<TextBox>(passwords).Any(), "passwords use masked native fields without plaintext editors");
            var password = Descendants<PasswordBox>(passwords).First(); password.Focus(); password.Password = "synthetic secret";
            tiles[4].Focus(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(store.Load().ArchivePasswords[0] == "synthetic secret", "masked edit persists through the existing ordered setting");
            var addPassword = Descendants<Button>(passwords).Single(button => button.Content as string == "+  Add password");
            addPassword.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var extra = Descendants<PasswordBox>(passwords).Last(); extra.Password = " Case Sensitive "; tiles[1].Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(store.Load().ArchivePasswords.Last() == " Case Sensitive ", "adding a masked entry retains exact case and spaces");
            Descendants<Button>(passwords).Last(button => button.Content as string == "Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Load().ArchivePasswords.Count == 3, "removing a masked entry preserves the other entries");
            password = Descendants<PasswordBox>(passwords).First(); password.Focus(); password.Password = new string('x', 1025); tiles[1].Focus();
            Check(System.Windows.Controls.Validation.GetHasError(passwords) && store.Load().ArchivePasswords[0] == "synthetic secret", "invalid masked edits report validation and preserve the last saved list");
            password.Password = "synthetic secret"; passwords.GetBindingExpression(MaskedPasswordList.TextProperty)!.UpdateSource();
            Check(!System.Windows.Controls.Validation.GetHasError(passwords), "correcting a masked edit clears validation");
            Select("Image installation"); Check(((TextBox)window.FindName("InstalledRootBox")).IsVisible, "installation controls remain available");
            var installed = (TextBox)window.FindName("InstalledRootBox"); installed.Text = Path.Combine(root, "installed separately");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(store.Load().InstalledGamesRoot == installed.Text && store.Load().DownloadRoot == download.Text, "installation path persists independently of downloads");
            var retain = Descendants<CheckBox>(workspace).Single(control => control.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path == "RetainOriginalImage");
            retain.IsChecked = true; Check(store.Load().RetainOriginalImage, "retention toggle uses existing persistence");
            Select("SharpEmu"); Check(((TextBox)window.FindName("SharpEmuPathBox")).IsVisible, "independent SharpEmu settings are reachable");
            var fullscreenOption = Descendants<CheckBox>(workspace).Single(control => control.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path == "SharpEmuFullscreen");
            fullscreenOption.IsChecked = true; Check(store.Load().SharpEmuFullscreen, "SharpEmu launch preference persists independently");
            await Capture("settings-sharpemu.png");
            Select("KytyPS5");
            var width = (TextBox)window.FindName("ArgumentWidthBox");
            Check(width.IsVisible && categories.SelectedItem is TabItem { Header: "KytyPS5" }, "Arguments are grouped with emulator settings");
            model.PerformanceArgumentsEnabled = true; model.ArgumentWidth = "1440"; model.ArgumentVblank = "60";
            await Capture("settings-desktop.png");
            await GraphicalSettingsTests.RunAsync(window, model, store, root);
            model.Interface = "Classic Windows"; classicTab.IsSelected = true; window.UpdateLayout();
            Check(ReferenceEquals(classicTab.Content, workspace) && width.IsVisible, "Classic uses the same tabs and preserved selection after returning");
            window.Width = 920; window.Height = 620; window.UpdateLayout();
            Check(width.IsVisible && categories.ActualWidth <= window.ActualWidth, "settings fits minimum desktop size with per-tab scrolling");
            foreach (var (sizeName, windowWidth, windowHeight) in new[] { ("minimum", 920d, 620d), ("normal", 1280d, 800d) })
            {
                window.Width = windowWidth; window.Height = windowHeight; window.UpdateLayout();
                foreach (var name in names.Take(7))
                {
                    Select(name);
                    var scroll = (ScrollViewer)((TabItem)categories.SelectedItem).Content;
                    Check(scroll.ActualHeight >= 150 && scroll.ExtentWidth <= scroll.ViewportWidth + 1, "readable vertical viewport at " + sizeName + ": " + name + $" (height={scroll.ActualHeight}, extent={scroll.ExtentWidth}, viewport={scroll.ViewportWidth})");
                    scroll.ScrollToBottom(); window.UpdateLayout();
                    Check(scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1, "all content reachable at " + sizeName + ": " + name);
                    scroll.ScrollToTop();
                    await Capture("settings-" + sizeName + "-" + categories.SelectedIndex + ".png");
                }
            }
            window.Width = 920; window.Height = 620; Select("KytyPS5");
            await Capture("settings-minimum.png");
            Select("SharpEmu");
            await Capture("settings-sharpemu-minimum.png");
            window.Width = 1280; window.Height = 800; window.UpdateLayout();
            await Capture("settings-sharpemu-normal.png");
            window.WindowState = WindowState.Maximized; window.UpdateLayout();
            await Capture("settings-sharpemu-maximized.png");
            window.Close(); window = null;
            var probe = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PS5CatalogBrowser.BrowserTests.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            probe.ArgumentList.Add("--settings-restart"); probe.ArgumentList.Add(store.DirectoryPath);
            using (var child = System.Diagnostics.Process.Start(probe)!)
            {
                var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Check(child.ExitCode == 0 && (await output).Contains("PASS separate-process settings restart"), "settings survive a separate-process restart: " + await errors);
            }
            using var restarted = new MainViewModel(store);
            Check(restarted.ArgumentWidth == "1440" && restarted.DownloadRoot.EndsWith("changed downloads") && restarted.Settings.ArchivePasswords[0] == "synthetic secret" && restarted.DownloadBandwidthMBps == 75 && restarted.Settings.SharpEmuFullscreen && restarted.Settings.RetainOriginalImage && restarted.InstalledGamesRoot.EndsWith("installed separately"), "both interfaces retain persisted edits on restart");
            Console.WriteLine("PASS Settings navigation: Classic controls and graphical page, masked passwords, Kyty arguments, fullscreen, Back/Escape, separate-process persistence and minimum window size");

            void Select(string name) { var index = Array.IndexOf(names, name); if (index < 7) tiles[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); else ((Button)window!.FindName("AboutSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window!.UpdateLayout(); }
            async Task Capture(string name)
            {
                if (Environment.GetEnvironmentVariable("PS5_SETTINGS_EVIDENCE") is not { Length: > 0 } evidence) return;
                await window!.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(evidence); using var file = File.Create(Path.Combine(evidence, name)); png.Save(file);
            }
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
            if (Directory.Exists(root)) throw new IOException("Settings navigation fixture remains in use: " + root);
        }
    }
    public static void VerifyRestart(string directory)
    {
        Check(Path.GetFileName(Path.GetDirectoryName(directory)!).StartsWith("PS5SettingsTabs-"), "restart probe is restricted to its synthetic fixture");
        using var model = new MainViewModel(new SettingsStore(directory));
        Check(model.ArgumentWidth == "1440" && model.DownloadRoot.EndsWith("changed downloads") && model.InstalledGamesRoot.EndsWith("installed separately") &&
            model.DownloadBandwidthMBps == 75 && model.Settings.ArchivePasswords[0] == "synthetic secret" && model.Settings.SharpEmuFullscreen && model.Settings.RetainOriginalImage &&
            model.Settings.StartupFullscreen && model.Settings.IgdbClientId == "updated-offline-fixture" && model.Settings.IgdbClientSecretProtected.Length > 0 && model.Sources.Single().Name == "Graphical source draft",
            "restarted process reads the persisted Settings values");
        Console.WriteLine("PASS separate-process settings restart");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!condition()) await Task.Delay(50, timeout.Token); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
