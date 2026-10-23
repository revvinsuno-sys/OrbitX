using System.Globalization;
using System.Windows.Data;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed class LibraryCaptionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is string id && values[1] is System.Collections.IEnumerable library &&
        library.OfType<LocalLibraryEntry>().Any(item => item.CatalogEntryId == id) ? "Related copy in Library" : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
