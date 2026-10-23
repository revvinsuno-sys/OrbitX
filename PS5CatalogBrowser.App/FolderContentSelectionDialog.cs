using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PS5CatalogBrowser.App;

public sealed class FolderContentSelectionDialog : Window
{
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];
    internal List<(string Path, System.Windows.Controls.Primitives.ToggleButton Control)> Choices { get; } = [];
    internal Button ConfirmButton { get; } = new();
    public FolderContentSelectionDialog(string title, string root, IReadOnlyList<string> paths, bool install, bool retain = true)
    {
        Title = install ? "Install packages — " + title : "Launch game folder — " + title;
        Width = 820; Height = 460; MinWidth = 560; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)); Foreground = Brushes.White;
        var panel = new DockPanel { Margin = new Thickness(18) }; Content = panel;
        var heading = new TextBlock { Text = install ? "Choose one or more unextracted PKGs to install." : "Choose the extracted game folder to launch.",
            TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var explanation = new TextBlock { Text = install
            ? "Each package is extracted into a separate folder here:\n" + root + "\nAfter verification, original PKGs are " + (retain ? "retained." : "deleted.")
            : root, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel", Foreground = Brushes.Black, IsCancel = true, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(5) }; buttons.Children.Add(cancel);
        ConfirmButton.Content = install ? "Install" : "Launch"; ConfirmButton.IsEnabled = false;
        ConfirmButton.Foreground = Brushes.Black; ConfirmButton.IsDefault = true;
        ConfirmButton.Padding = new Thickness(18, 7, 18, 7); ConfirmButton.Margin = new Thickness(5); buttons.Children.Add(ConfirmButton);
        ConfirmButton.Click += (_, _) => { SelectedPaths = Choices.Where(choice => choice.Control.IsChecked == true).Select(choice => choice.Path).ToArray(); if (SelectedPaths.Count > 0) DialogResult = true; };
        var list = new StackPanel(); panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var path in paths)
        {
            System.Windows.Controls.Primitives.ToggleButton choice = install ? new CheckBox() : new RadioButton { GroupName = "LaunchFolder" };
            choice.Content = new TextBlock { Text = Path.GetRelativePath(root, path), TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
            choice.Margin = new Thickness(5, 9, 5, 9); choice.Foreground = Brushes.White;
            choice.Checked += (_, _) => ConfirmButton.IsEnabled = Choices.Any(option => option.Control.IsChecked == true);
            choice.Unchecked += (_, _) => ConfirmButton.IsEnabled = Choices.Any(option => option.Control.IsChecked == true);
            Choices.Add((path, choice)); list.Children.Add(choice);
        }
    }
}
