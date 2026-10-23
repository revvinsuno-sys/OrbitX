using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    private void ViewLaunchLog_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var item = model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item first.");
        var viewer = new Window { Title = item.Title + " · Launch log", Owner = this, Width = 960, Height = 650, MinWidth = 440, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(16) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Text = model.ReadLaunchLog(item) };
        var refresh = new Button { Content = "Refresh log" }; refresh.Click += (_, _) => Run(() => text.Text = model.ReadLaunchLog(item));
        var close = new Button { Content = "Close" }; close.Click += (_, _) => viewer.Close();
        controls.Children.Add(refresh); controls.Children.Add(close); DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls); root.Children.Add(text);
        viewer.Content = root; viewer.Show();
    });
}
