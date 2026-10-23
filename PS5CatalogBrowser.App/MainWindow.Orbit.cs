using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    private OrbitFrontendHost? orbitFrontend;
    private bool temporarilyClassic;
    private bool fullscreen;
    private Rect beforeFullscreen;
    private WindowState beforeFullscreenState;
    private WindowStyle beforeFullscreenStyle;
    private ResizeMode beforeFullscreenResize;
    private bool frontendFailed;
    private bool windowClosed;
    private bool enteringFullscreen, chromeRevealed, chromeKeyboardMode;
    private readonly DispatcherTimer fullscreenPointer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    private void ConfigureFrontend()
    {
        // WebView2 is a native child window, so WPF mouse events do not cover its surface.
        fullscreenPointer.Tick += (_, _) =>
        {
            if (GetCursorPos(out var point)) UpdateFullscreenChrome(PointFromScreen(new Point(point.X, point.Y)), IsActive);
        };
        Closed += (_, _) => fullscreenPointer.Stop();
        if (model.Settings.WindowPlacement is { } saved &&
            new[] { saved.Left, saved.Top, saved.Width, saved.Height }.All(double.IsFinite) &&
            saved.Width >= MinWidth && saved.Height >= MinHeight)
        {
            Width = Math.Min(saved.Width, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(saved.Height, SystemParameters.VirtualScreenHeight);
            Left = Math.Clamp(saved.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(saved.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height));
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
            else if (e.Key == Key.F6 && fullscreen) { RevealFullscreenToolbar(); e.Handled = true; }
            else if (e.Key == Key.F2) { OrbitAppearance_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Escape && fullscreen && OrbitSurface.Visibility != Visibility.Visible && Keyboard.FocusedElement is not TextBoxBase)
            { ExitFullscreen(); e.Handled = true; }
        };
    }
    private async Task ApplyInterfaceAsync()
    {
        if (windowClosed) return;
        temporarilyClassic = false;
        if (model.Interface != "Orbit Store" || frontendFailed) { ShowClassic(); return; }
        OrbitSurface.Visibility = Visibility.Visible; ClassicTabs.Visibility = Visibility.Collapsed;
        NativeDownloadToast.Visibility = Visibility.Collapsed;
        ReturnToOrbit.Visibility = Visibility.Collapsed;
        if (orbitFrontend is null)
        {
            orbitFrontend = new(); OrbitSurface.Content = orbitFrontend;
            orbitFrontend.PageRequested += OpenWindowsPage;
            orbitFrontend.ActionRequested += RunOrbitAction;
            orbitFrontend.SettingsActionRequested += RunOrbitSettingsAction;
            orbitFrontend.SourceEditorAvailable = () => SourceEditorPanel.IsEnabled;
            orbitFrontend.FullscreenRequested += toggle => { if (toggle) ToggleFullscreen(); else ExitFullscreen(); };
            orbitFrontend.ToolbarRequested += RevealFullscreenToolbar;
            orbitFrontend.Failed += message => { frontendFailed = true; model.Status = message; ShowClassic(); };
            orbitFrontend.ExternalLinkRequested += url =>
            {
                if (MessageBox.Show(this, "Open this link in your browser?\n\n" + url, "External website", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                    Run(() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
            };
        }
        try
        {
            await orbitFrontend.InitializeAsync(model);
            if (!windowClosed && model.Interface == "Orbit Store" && !temporarilyClassic && !frontendFailed)
            { orbitFrontend.Browser.Focus(); orbitFrontend.NotifyFullscreen(fullscreen); }
        }
        catch (Exception ex)
        { if (!windowClosed) { frontendFailed = true; model.Status = "Orbit is unavailable: " + ex.Message + " Classic Windows is available. Restart to retry Orbit."; ShowClassic(); } }
    }
    private void ShowClassic()
    {
        ExitFullscreen();
        OrbitSurface.Visibility = Visibility.Collapsed; ClassicTabs.Visibility = Visibility.Visible;
        ReturnToOrbit.Visibility = model.Interface == "Orbit Store" && !frontendFailed ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OpenWindowsPage(string page)
    {
        if (page == "Classic Windows") { model.Interface = "Classic Windows"; return; }
        if (model.Interface == "Orbit Store" && !frontendFailed)
        {
            orbitFrontend?.NavigatePage(page == "My Library" ? "Library" : page);
            return;
        }
        temporarilyClassic = true; ShowClassic();
        if (ClassicTabs.Items.Cast<TabItem>().FirstOrDefault(tab => tab.Header as string == page) is { } target)
        { target.IsSelected = true; target.Focus(); }
    }
    private void WindowsPage_Click(object sender, RoutedEventArgs e) => OpenWindowsPage((string)((Button)sender).Tag);
    private async void OrbitAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (model.Interface != "Orbit Store") { InterfacePicker.Focus(); InterfacePicker.IsDropDownOpen = true; return; }
        await ApplyInterfaceAsync();
        if (!frontendFailed && !windowClosed) orbitFrontend?.ShowAppearance();
    }
    private async void ReturnToOrbit_Click(object sender, RoutedEventArgs e) => await ApplyInterfaceAsync();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private async void ToggleFullscreen() => await RunAsync(async () =>
    {
        if (fullscreen) { ExitFullscreen(); return; }
        if (enteringFullscreen) return;
        enteringFullscreen = true;
        try
        {
            if (model.Interface != "Orbit Store") model.Interface = "Orbit Store";
            await ApplyInterfaceAsync();
            if (frontendFailed || windowClosed || OrbitSurface.Visibility != Visibility.Visible) return;
            beforeFullscreen = WindowState == WindowState.Normal ? new(Left, Top, Width, Height) : RestoreBounds;
            beforeFullscreenState = WindowState; beforeFullscreenStyle = WindowStyle; beforeFullscreenResize = ResizeMode;
            fullscreen = true; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowsSettingsButton.Visibility = Visibility.Collapsed;
            ApplicationLayout.Margin = new Thickness(0); NativeStatus.Visibility = Visibility.Collapsed;
            NativeChrome.Height = 0; NativeChrome.IsHitTestVisible = NativeChrome.IsEnabled = false;
            chromeRevealed = chromeKeyboardMode = false;
            WindowState = WindowState.Maximized; orbitFrontend?.NotifyFullscreen(true); fullscreenPointer.Start();
        }
        finally { enteringFullscreen = false; }
    });
    internal void UpdateFullscreenChrome(Point position, bool active)
    {
        if (!fullscreen) return;
        var inside = position.X >= 0 && position.X <= ApplicationLayout.ActualWidth && position.Y >= 0;
        var reveal = active && (InterfacePicker.IsDropDownOpen || chromeKeyboardMode && NativeChrome.IsKeyboardFocusWithin ||
            inside && position.Y <= (chromeRevealed ? Math.Max(NativeChrome.ActualHeight, NativeToolbar.DesiredSize.Height) + 8 : 4));
        SetFullscreenToolbar(reveal);
    }
    internal void RevealFullscreenToolbar()
    {
        if (!fullscreen) return;
        chromeKeyboardMode = true; SetFullscreenToolbar(true); InterfacePicker.Focus();
    }
    private void SetFullscreenToolbar(bool reveal)
    {
        if (chromeRevealed == reveal) return;
        chromeRevealed = reveal;
        if (!reveal) chromeKeyboardMode = false;
        NativeToolbar.Measure(new Size(ApplicationLayout.ActualWidth, double.PositiveInfinity));
        var height = reveal ? NativeToolbar.DesiredSize.Height : 0;
        var previous = NativeChrome.ActualHeight;
        NativeChrome.IsHitTestVisible = NativeChrome.IsEnabled = reveal;
        NativeChrome.Height = height;
        NativeChrome.BeginAnimation(HeightProperty, SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(previous, height, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
            : null);
    }
    private void ExitFullscreen()
    {
        if (!fullscreen) return;
        fullscreenPointer.Stop(); chromeRevealed = chromeKeyboardMode = false;
        NativeChrome.BeginAnimation(HeightProperty, null); NativeChrome.Height = double.NaN; NativeChrome.IsHitTestVisible = NativeChrome.IsEnabled = true;
        NativeStatus.Visibility = Visibility.Visible; ApplicationLayout.Margin = new Thickness(12);
        WindowsSettingsButton.Visibility = Visibility.Visible;
        fullscreen = false; WindowState = WindowState.Normal; WindowStyle = beforeFullscreenStyle; ResizeMode = beforeFullscreenResize;
        Left = beforeFullscreen.Left; Top = beforeFullscreen.Top; Width = beforeFullscreen.Width; Height = beforeFullscreen.Height;
        WindowState = beforeFullscreenState; orbitFrontend?.NotifyFullscreen(false);
    }
    private void SavePlacement()
    {
        var bounds = fullscreen ? beforeFullscreen : WindowState == WindowState.Normal ? new(Left, Top, Width, Height) : RestoreBounds;
        try { model.SaveWindowPlacement(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            (fullscreen ? beforeFullscreenState : WindowState) == WindowState.Maximized)); }
        catch (Exception ex) { Trace.WriteLine("Could not save window placement: " + ex.Message); }
    }
}
