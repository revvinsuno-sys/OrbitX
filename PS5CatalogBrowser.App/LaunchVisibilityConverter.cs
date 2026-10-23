using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed class LaunchVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LocalLibraryEntry item && (item.LaunchPaths.Count > 0 || !item.FolderActionsInspected && item.Classification is "Ready for launch attempt" or "Direct ELF launch attempt" or "Installed — Ready for launch attempt")
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
