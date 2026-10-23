using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    private bool launchInteraction;
    private async Task LaunchGameAsync(bool chooseEmulator, EmulatorBackend? selectedEmulator = null)
    {
        if (launchInteraction) return;
        launchInteraction = true;
        try
        {
            var item = model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item.");
            var backend = selectedEmulator;
            if (chooseEmulator && backend is null)
            {
                var dialog = new EmulatorSelectionDialog(item.Title, model) { Owner = this };
                var selected = dialog.ShowDialog() == true;
                if (OrbitSurface.IsVisible) orbitFrontend?.Browser.Focus();
                if (!selected) return;
                backend = dialog.Selected;
            }
            await model.RefreshFolderActionsAsync(item);
            string? selectedPath = null;
            if (item.LaunchPaths.Count > 1)
            {
                var picker = new FolderContentSelectionDialog(item.Title, item.ContentPath, item.LaunchPaths, install: false) { Owner = this };
                if (picker.ShowDialog() != true) return;
                selectedPath = picker.SelectedPaths.Single();
            }
            if ((backend ?? item.LastEmulator) == EmulatorBackend.KytyPS5 && item.Classification == "Direct ELF launch attempt" &&
                (item.Source.Contains("evoX", StringComparison.OrdinalIgnoreCase) || item.Title.Contains("payload", StringComparison.OrdinalIgnoreCase)) &&
                MessageBox.Show("This ELF appears to be a PS5 console payload. KytyPS5 may not accept it as a standalone game. Attempt launch?", "Unverified ELF", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            model.Launch(item, selectedPath, backend);
        }
        finally { launchInteraction = false; }
    }
    private async void ChooseGameEmulator_Click(object sender, RoutedEventArgs e) => await RunAsync(() => LaunchGameAsync(true));
    private void ChooseSharpEmu_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog { Filter = "SharpEmu (SharpEmu.exe)|SharpEmu.exe" };
        if (picker.ShowDialog(this) == true) model.SharpEmuPath = picker.FileName;
    });
    private void ChooseShadPS4_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog { Filter = "shadPS4 (shadPS4.exe)|shadPS4.exe" };
        if (picker.ShowDialog(this) == true) model.ShadPS4Path = picker.FileName;
    });
    private void TestShadPS4_Click(object sender, RoutedEventArgs e) => Run(model.TestShadPS4);
    private async void CheckSharpEmu_Click(object sender, RoutedEventArgs e) => await model.CheckSharpEmuAsync();
    private async void UpdateSharpEmu_Click(object sender, RoutedEventArgs e) => await model.UpdateSharpEmuAsync();
    private void SharpEmuLog_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!File.Exists(model.SharpEmuBuildLog)) throw new IOException("No SharpEmu build log is available.");
        Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { model.SharpEmuBuildLog }, UseShellExecute = false });
    });

    private DispatcherTimer? coverHold;
    private Point coverOrigin;
    private FrameworkElement? coverTarget;
    private bool coverConsumed;
    private TouchDevice? coverTouch;
    private void CancelCoverHold()
    {
        coverHold?.Stop(); coverHold = null;
        var target = coverTarget;
        coverTarget = null;
        var touch = coverTouch; coverTouch = null;
        if (ReferenceEquals(Mouse.Captured, target) && target is not null) Mouse.Capture(null);
        if (touch is not null && ReferenceEquals(touch.Captured, target) && target is not null) touch.Capture(null);
    }
    private void CoverDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || launchInteraction) return;
        CancelCoverHold(); coverConsumed = false;
        coverTarget = (FrameworkElement)sender; coverOrigin = e.GetPosition(coverTarget);
        model.SelectedLibraryEntry = (LocalLibraryEntry)coverTarget.DataContext;
        coverTarget.CaptureMouse(); e.Handled = true;
        coverHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        coverHold.Tick += async (_, _) =>
        {
            coverConsumed = true; CancelCoverHold();
            await RunAsync(() => LaunchGameAsync(true));
        };
        coverHold.Start();
    }
    private void CoverMove(object sender, MouseEventArgs e)
    {
        if (coverTarget is null) return;
        var movement = e.GetPosition(coverTarget) - coverOrigin;
        if (movement.Length > 10) { coverConsumed = true; CancelCoverHold(); }
    }
    private async void CoverUp(object sender, MouseButtonEventArgs e)
    {
        var click = coverTarget is not null && !coverConsumed;
        CancelCoverHold(); e.Handled = true;
        if (click) await RunAsync(() => LaunchGameAsync(false));
    }
    private void CoverTouchDown(object sender, TouchEventArgs e)
    {
        if (launchInteraction || coverTarget is not null) return;
        coverConsumed = false; coverTarget = (FrameworkElement)sender; coverTouch = e.TouchDevice;
        coverOrigin = e.GetTouchPoint(coverTarget).Position;
        coverTouch.Capture(coverTarget); e.Handled = true;
        model.SelectedLibraryEntry = (LocalLibraryEntry)coverTarget.DataContext;
        coverHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        coverHold.Tick += async (_, _) => { coverConsumed = true; CancelCoverHold(); await RunAsync(() => LaunchGameAsync(true)); };
        coverHold.Start();
    }
    private void CoverTouchMove(object sender, TouchEventArgs e)
    {
        if (coverTarget is not null && e.TouchDevice == coverTouch && (e.GetTouchPoint(coverTarget).Position - coverOrigin).Length > 10)
        { coverConsumed = true; CancelCoverHold(); }
        e.Handled = true;
    }
    private async void CoverTouchUp(object sender, TouchEventArgs e)
    {
        var click = coverTarget is not null && e.TouchDevice == coverTouch && !coverConsumed;
        CancelCoverHold(); e.Handled = true;
        if (click) await RunAsync(() => LaunchGameAsync(false));
    }
    private void CoverTouchCancelled(object sender, TouchEventArgs e) { coverConsumed = true; CancelCoverHold(); }
    private void CoverCancelled(object sender, MouseEventArgs e) { coverConsumed = true; CancelCoverHold(); }
}
