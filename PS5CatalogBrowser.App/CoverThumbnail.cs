using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

// Only fetch artwork near the viewport; catalogues can contain thousands of tiles.
public sealed class CoverThumbnail : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(nameof(Url), typeof(string), typeof(CoverThumbnail),
        new PropertyMetadata("", (d, _) => ((CoverThumbnail)d).Reset()));
    public string Url { get => (string)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public static readonly DependencyProperty FallbackUrlProperty = DependencyProperty.Register(nameof(FallbackUrl), typeof(string), typeof(CoverThumbnail),
        new PropertyMetadata("", (d, _) => ((CoverThumbnail)d).Reset()));
    public string FallbackUrl { get => (string)GetValue(FallbackUrlProperty); set => SetValue(FallbackUrlProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock placeholder = new() { Text = "No artwork", Foreground = new SolidColorBrush(Color.FromRgb(166, 181, 202)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private ScrollViewer? viewer;
    private CancellationTokenSource? pending;
    private string? attempted;

    public CoverThumbnail()
    {
        Background = new SolidColorBrush(Color.FromRgb(25, 35, 52));
        Children.Add(placeholder); Children.Add(image);
        SizeChanged += (_, _) => Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        Loaded += (_, _) =>
        {
            DependencyObject? parent = this;
            while ((parent = VisualTreeHelper.GetParent(parent)) is not null)
                if (parent is ScrollViewer scroll) { viewer = scroll; viewer.ScrollChanged += OnScroll; break; }
            CheckViewport();
        };
        Unloaded += (_, _) =>
        {
            if (viewer is not null) viewer.ScrollChanged -= OnScroll;
            viewer = null; pending?.Cancel(); attempted = null;
        };
    }
    private void Reset() { pending?.Cancel(); attempted = null; image.Source = null; placeholder.Visibility = Visibility.Visible; CheckViewport(); }
    private void OnScroll(object sender, ScrollChangedEventArgs e) => CheckViewport();
    private async void CheckViewport()
    {
        // Filtering can detach a tile before WPF delivers its Unloaded event.
        if (!IsLoaded || viewer is null || !viewer.IsAncestorOf(this) || string.IsNullOrWhiteSpace(Url) && string.IsNullOrWhiteSpace(FallbackUrl)) return;
        var bounds = TransformToAncestor(viewer).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
        var visible = bounds.IntersectsWith(new Rect(0, -200, viewer.ActualWidth, viewer.ActualHeight + 400));
        if (!visible)
        {
            pending?.Cancel(); attempted = null; image.Source = null; placeholder.Visibility = Visibility.Visible;
            return;
        }
        var identity = Url + "\n" + FallbackUrl;
        if (attempted == identity) return;
        var urls = new[] { Url, FallbackUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToArray();
        attempted = identity;
        using var request = new CancellationTokenSource(); pending = request;
        try
        {
            foreach (var url in urls)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var bitmap = await CoverImages.LoadAsync(url, timeout.Token);
                    if (!request.IsCancellationRequested && Url + "\n" + FallbackUrl == identity)
                    { image.Source = bitmap; placeholder.Visibility = Visibility.Collapsed; }
                    break;
                }
                catch when (!request.IsCancellationRequested) { /* Try the catalogue's explicit fallback. */ }
            }
        }
        catch { /* Optional artwork leaves an explicit placeholder. */ }
        finally { if (pending == request) pending = null; }
    }
}

internal static class CoverImages
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly HttpTransfer Transfer = new();
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly Dictionary<string, BitmapSource> Cache = [];
    private static readonly Queue<string> Order = new();

    public static async Task<BitmapSource> LoadAsync(string url, CancellationToken token)
    {
        lock (Cache) if (Cache.TryGetValue(url, out var cached)) return cached;
        await Gate.WaitAsync(token);
        try
        {
            lock (Cache) if (Cache.TryGetValue(url, out var cached)) return cached;
            using var response = await Transfer.GetAsync(url, token);
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Artwork exceeds 4 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var block = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(block, token)) > 0)
            {
                if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Artwork exceeds 4 MiB.");
                buffer.Write(block, 0, count);
            }
            var bitmap = await Task.Run(() =>
            {
                buffer.Position = 0;
                var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > 8192 || frame.PixelHeight > 8192 || (long)frame.PixelWidth * frame.PixelHeight > 24_000_000)
                    throw new InvalidDataException("Artwork dimensions exceed limits.");
                buffer.Position = 0;
                var result = new BitmapImage(); result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
                result.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * Math.Min(1d, Math.Min(320d / frame.PixelWidth, 480d / frame.PixelHeight)))); result.StreamSource = buffer; result.EndInit(); result.Freeze();
                return result;
            }, token);
            lock (Cache)
            {
                if (!Cache.ContainsKey(url)) { Cache.Add(url, bitmap); Order.Enqueue(url); }
                while (Order.Count > 128) Cache.Remove(Order.Dequeue());
            }
            return bitmap;
        }
        finally { Gate.Release(); }
    }
}
