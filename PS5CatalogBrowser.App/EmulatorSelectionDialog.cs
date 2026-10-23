using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

internal sealed class EmulatorSelectionDialog : Window
{
    internal EmulatorBackend? Selected { get; private set; }
    internal EmulatorSelectionDialog(string title, MainViewModel model)
    {
        Title = "Choose Emulator"; Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Choose Emulator", FontSize = 22, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        foreach (var backend in new[] { EmulatorBackend.KytyPS5, EmulatorBackend.SharpEmu })
        {
            var availability = backend == EmulatorBackend.KytyPS5 ? model.EmulatorAvailability : model.SharpEmuAvailability;
            panel.Children.Add(new TextBlock { Text = backend + ": " + availability, TextWrapping = TextWrapping.Wrap });
            var button = new Button { Content = backend.ToString(), Foreground = Brushes.Black, MinHeight = 44, Margin = new Thickness(0, 6, 0, 14) };
            System.Windows.Automation.AutomationProperties.SetName(button, "Launch with " + backend);
            button.Click += (_, _) => { Selected = backend; DialogResult = true; };
            panel.Children.Add(button);
        }
        var cancel = new Button { Content = "Cancel", Foreground = Brushes.Black, IsCancel = true, MinHeight = 44 };
        panel.Children.Add(cancel); Loaded += (_, _) => cancel.Focus();
    }
}
