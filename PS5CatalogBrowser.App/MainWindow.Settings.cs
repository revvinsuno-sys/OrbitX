using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    private bool settingsHome = true;
    private bool settingsInitialized;
    private readonly List<Button> settingsCategoryButtons = [];

    private void ConfigureSettings()
    {
        // Only artwork is sampled from the supplied sheet. All labels and controls remain live.
        var sheet = new BitmapImage(new Uri("pack://application:,,,/OrbitX;component/Assets/Settings/components.png"));
        var regions = new[] {
            new Int32Rect(40, 46, 386, 113), new Int32Rect(450, 46, 372, 113),
            new Int32Rect(848, 46, 374, 113), new Int32Rect(1248, 46, 386, 113),
            new Int32Rect(40, 250, 386, 108), new Int32Rect(452, 250, 372, 108),
            new Int32Rect(848, 250, 374, 108), new Int32Rect(66, 852, 62, 66)
        };
        var iconRegions = new[] { new Int32Rect(88, 62, 96, 94), new Int32Rect(508, 64, 74, 90), new Int32Rect(910, 60, 78, 98),
            new Int32Rect(1303, 65, 99, 91), new Int32Rect(90, 267, 104, 86), new Int32Rect(493, 270, 117, 82), new Int32Rect(910, 262, 76, 100), new Int32Rect(66, 852, 62, 66) };
        string?[] summaries = [null, null, null, null, nameof(MainViewModel.InstallationAvailability), nameof(MainViewModel.SharpEmuAvailability), nameof(MainViewModel.EmulatorAvailability), nameof(MainViewModel.AboutBuildVersion)];
        for (var i = 0; i < SettingsTabs.Items.Count; i++)
        {
            var index = i;
            var art = new CroppedBitmap(sheet, regions[i]); art.Freeze();
            if (((TabItem)SettingsTabs.Items[i]).Content is ScrollViewer { Content: StackPanel panel })
            {
                foreach (var section in SettingsSections(panel))
                {
                    var heading = new DockPanel();
                    var icon = new CroppedBitmap(sheet, iconRegions[i]); icon.Freeze();
                    heading.Children.Add(new Image { Source = icon, Width = 40, Height = 40, Margin = new Thickness(0, 0, 16, 0), Stretch = Stretch.Uniform });
                    var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    titles.Children.Add(new TextBlock { Text = section.Header?.ToString(), Foreground = Brushes.White, FontSize = 22, Margin = new Thickness(0) });
                    if (section.Content is StackPanel body && body.Children.Count > 0 && body.Children[0] is TextBlock subtitle && subtitle.Margin.Bottom == 14)
                    {
                        body.Children.Remove(subtitle);
                        subtitle.Margin = new Thickness(0, 3, 0, 0); subtitle.FontSize = 14;
                        titles.Children.Add(subtitle);
                    }
                    heading.Children.Add(titles);
                    section.Header = heading;
                }
            }
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(new Image { Source = art, Stretch = i == 7 ? Stretch.Uniform : Stretch.UniformToFill, ClipToBounds = true,
                HorizontalAlignment = i == 7 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch, Margin = i == 7 ? new Thickness(20, 6, 0, 0) : new Thickness(0) });
            var caption = new StackPanel { Margin = new Thickness(12, 6, 20, 10) };
            caption.Children.Add(new TextBlock { Text = (string)((TabItem)SettingsTabs.Items[i]).Header, Foreground = Brushes.White, FontSize = 15, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            var summary = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(173, 209, 245)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            if (summaries[i] is { } binding) summary.SetBinding(TextBlock.TextProperty, new Binding(binding));
            else if (i == 0) summary.SetBinding(TextBlock.TextProperty, new Binding("Sources.Count") { StringFormat = "Sources: {0}" });
            // Do not claim readiness for unconfigured or unsupported features.
            else summary.Text = i switch { 1 => "Storage and bandwidth", 2 => "Saved extraction passwords", _ => "Site sessions and data" };
            summary.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = summary });
            caption.Children.Add(summary); Grid.SetRow(caption, 1); content.Children.Add(caption);
            var arrow = new TextBlock { Text = "›", Foreground = Brushes.White, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 18) };
            Grid.SetRow(arrow, 1); content.Children.Add(arrow);
            var button = new Button { Content = content, Style = (Style)FindResource("SettingsTile"), Height = 166 };
            AutomationProperties.SetName(button, (string)((TabItem)SettingsTabs.Items[i]).Header);
            button.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(TextBlock.Text)) { Source = summary });
            var tip = new MultiBinding { StringFormat = "{0}\n{1}" };
            tip.Bindings.Add(new Binding { Source = (string)((TabItem)SettingsTabs.Items[i]).Header });
            tip.Bindings.Add(new Binding(nameof(TextBlock.Text)) { Source = summary });
            button.SetBinding(ToolTipProperty, tip);
            if (i == 7) RegisterName("AboutSettingsButton", button);
            button.Click += (_, _) => SelectSettingsCategory(index);
            button.PreviewKeyDown += (_, e) =>
            {
                var count = settingsCategoryButtons.Count;
                var next = e.Key switch { Key.Right => (index + 1) % count, Key.Left => (index + count - 1) % count, Key.Home => 0, Key.End => count - 1, _ => -1 };
                if (next >= 0) { settingsCategoryButtons[next].Focus(); e.Handled = true; }
            };
            settingsCategoryButtons.Add(button); SettingsTiles.Children.Add(button);
        }
        foreach (var tab in SettingsTabs.Items.Cast<TabItem>())
            foreach (var action in SettingsActions((DependencyObject)tab.Content))
            {
                if (action.Content is not string label) continue;
                Int32Rect? region = label.Contains("folder", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Choose") ? new Int32Rect(72, 789, 31, 32)
                    : label.StartsWith("Check for updates") ? new Int32Rect(82, 570, 45, 36)
                    : label.StartsWith("Update") || label.StartsWith("Refresh") ? new Int32Rect(441, 572, 40, 34)
                    : label.Contains("log", StringComparison.OrdinalIgnoreCase) ? new Int32Rect(891, 573, 34, 35) : null;
                if (region is { } bounds) { var icon = new CroppedBitmap(sheet, bounds); icon.Freeze(); action.Resources["SettingsActionIcon"] = icon; }
            }
        settingsInitialized = true;
        ShowSettingsHome();
    }

    private void SettingsTiles_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutSettingsTiles();
    private void SettingsTiles_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) LayoutSettingsTiles();
    }
    private static IEnumerable<Button> SettingsActions(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button action) yield return action;
            foreach (var descendant in SettingsActions(child)) yield return descendant;
        }
    }
    private void SettingsWorkspace_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutSettingsTiles();
    private static IEnumerable<GroupBox> SettingsSections(StackPanel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is GroupBox section) yield return section;
            else if (child is StackPanel nested)
                foreach (var descendant in SettingsSections(nested)) yield return descendant;
        }
    }
    private void LayoutSettingsTiles()
    {
        if (!settingsInitialized) return;
        var width = Math.Max(300, SettingsTileScroll.ViewportWidth > 0 ? SettingsTileScroll.ViewportWidth : SettingsTileScroll.ActualWidth);
        var shortWindow = SettingsWorkspace.ActualHeight < 600;
        SettingsHeading.FontSize = shortWindow ? 52 : SettingsWorkspace.ActualHeight < 850 ? 76 : 104;
        SettingsHeading.LineHeight = SettingsHeading.FontSize * 1.1;
        var factColumns = width < 1200 ? 1 : 2;
        if (SharpFacts.ColumnDefinitions.Count != factColumns)
        {
            SharpFacts.ColumnDefinitions.Clear(); SharpFacts.RowDefinitions.Clear();
            for (var i = 0; i < factColumns; i++) SharpFacts.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < (SharpFacts.Children.Count + factColumns - 1) / factColumns; i++) SharpFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < SharpFacts.Children.Count; i++)
            {
                Grid.SetRow(SharpFacts.Children[i], i / factColumns); Grid.SetColumn(SharpFacts.Children[i], i % factColumns);
                ((Grid)SharpFacts.Children[i]).ColumnDefinitions[0].Width = new GridLength(factColumns == 1 ? 160 : 200);
            }
        }
        var columns = settingsHome ? Math.Clamp((int)(width / 260), 2, 4) : settingsCategoryButtons.Count;
        var tileWidth = settingsHome ? Math.Floor(width / columns) - 10 : Math.Max(156, Math.Floor(width / columns) - 10);
        SettingsTiles.Orientation = settingsHome ? Orientation.Horizontal : Orientation.Vertical;
        var detailHeight = shortWindow ? 102 : 124;
        SettingsTiles.Height = settingsHome ? double.NaN : detailHeight + 10;
        foreach (var tile in settingsCategoryButtons) { tile.Width = tileWidth; tile.Height = settingsHome ? 196 : detailHeight; }
        SettingsTileScroll.HorizontalScrollBarVisibility = settingsHome ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        SettingsTileScroll.VerticalScrollBarVisibility = settingsHome ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Home occupies the scrollable content region; details retain a separate scrolling viewport.
        Grid.SetRowSpan(SettingsTileScroll, settingsHome ? 2 : 1);
    }

    private void SettingsHome_Click(object sender, RoutedEventArgs e) => ShowSettingsHome();
    private void ShowSettingsHome()
    {
        if (!settingsInitialized) return;
        Keyboard.ClearFocus();
        settingsHome = true;
        SettingsTabs.Visibility = Visibility.Collapsed;
        SettingsBreadcrumb.Text = "Choose a category";
        foreach (var tile in settingsCategoryButtons) { tile.Tag = null; AutomationProperties.SetItemStatus(tile, ""); }
        LayoutSettingsTiles();
    }

    private void SelectSettingsCategory(int index)
    {
        // Commit LostFocus fields before hiding their existing visuals. Structured argument drafts stay drafts.
        Keyboard.ClearFocus();
        SettingsTabs.SelectedIndex = index;
        ShowSettingsCategory();
    }
    private void SettingsCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingsInitialized && e.OriginalSource == SettingsTabs) ShowSettingsCategory();
    }
    private void ShowSettingsCategory()
    {
        settingsHome = false;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsBreadcrumb.Text = "›  " + ((TabItem)SettingsTabs.SelectedItem).Header;
        for (var i = 0; i < settingsCategoryButtons.Count; i++)
        {
            var selected = i == SettingsTabs.SelectedIndex;
            settingsCategoryButtons[i].Tag = selected ? "Selected" : null;
            AutomationProperties.SetItemStatus(settingsCategoryButtons[i], selected ? "Selected" : "");
        }
        LayoutSettingsTiles();
        if (((TabItem)SettingsTabs.SelectedItem).Content is ScrollViewer scroll) scroll.ScrollToTop();
        settingsCategoryButtons[SettingsTabs.SelectedIndex].BringIntoView();
    }
}
