using System.Windows;
using System.Windows.Controls;

namespace PS5CatalogBrowser.App;

// Presentation of the existing ordered newline-separated setting. No second store or reveal action.
public sealed class MaskedPasswordList : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MaskedPasswordList),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MaskedPasswordList)d).Refresh()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private bool editing;
    private readonly StackPanel rows = new();

    public MaskedPasswordList()
    {
        Children.Add(rows);
        var add = new Button { Content = "+  Add password", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => { AddRow(""); rows.Children.OfType<DockPanel>().Last().Children.OfType<PasswordBox>().Single().Focus(); };
        Children.Add(add);
        Loaded += (_, _) => Refresh();
        LostKeyboardFocus += (_, _) => GetBindingExpression(TextProperty)?.UpdateSource();
    }

    private void Refresh()
    {
        if (editing) return;
        rows.Children.Clear();
        foreach (var password in Text.Replace("\r", "").Split('\n').Where(line => line.Length > 0)) AddRow(password);
    }

    private void AddRow(string password)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var remove = new Button { Content = "Remove", MinWidth = 90 };
        DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
        var field = new PasswordBox { Password = password, Padding = new Thickness(10, 8, 10, 8),
            Background = System.Windows.Media.Brushes.Transparent, Foreground = System.Windows.Media.Brushes.White, MinHeight = 38 };
        System.Windows.Automation.AutomationProperties.SetName(field, "Archive password " + (rows.Children.Count + 1));
        remove.Click += (_, _) => { rows.Children.Remove(row); Publish(); GetBindingExpression(TextProperty)?.UpdateSource(); };
        field.PasswordChanged += (_, _) => Publish();
        row.Children.Add(field); rows.Children.Add(row);
    }

    private void Publish()
    {
        editing = true;
        try { SetCurrentValue(TextProperty, string.Join(Environment.NewLine, rows.Children.OfType<DockPanel>().Select(row => row.Children.OfType<PasswordBox>().Single().Password))); }
        finally { editing = false; }
    }
}
