using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class CatalogPresentationUiTests
{
    public static async Task RunAsync()
    {
        using var fixture = new BrowserFixtures();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = new MemoryStream()) { encoder.Save(output); fixture.Artwork = output.ToArray(); }
        var tile = new CoverThumbnail { Width = 160, Height = 160, Url = fixture.Url("/cover") };
        var panel = new StackPanel(); panel.Children.Add(tile);
        var artworkWindow = new Window { Content = new ScrollViewer { Content = panel }, Width = 220, Height = 240, ShowActivated = false };
        try
        {
            artworkWindow.Show();
            await Wait(() => ((Image)tile.Children[1]).Source is BitmapSource);
            Check(((BitmapSource)((Image)tile.Children[1]).Source).PixelWidth == 2, "catalogue thumbnail is decoded and shown");
            tile.FallbackUrl = fixture.Url("/cover"); tile.Url = fixture.Url("/bad-cover");
            await Wait(() => fixture.Requests.ContainsKey("/bad-cover") && ((Image)tile.Children[1]).Source is BitmapSource);
            Check(((TextBlock)tile.Children[0]).Visibility == Visibility.Collapsed, "failed primary uses catalogue fallback");
            panel.Children.Remove(tile); tile.Url = fixture.Url("/cover?filtered");
            await artworkWindow.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            panel.Children.Add(tile); tile.Url = "";
            await Wait(() => ((Image)tile.Children[1]).Source is BitmapSource);
            Check(tile.FallbackUrl.Length > 0, "fallback-only artwork survives filtering and reload");
        }
        finally { artworkWindow.Close(); }

        var root = Path.Combine(Path.GetTempPath(), "PS5PopupPresentation-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "downloads") });
        var main = new MainWindow(store) { ShowActivated = false };
        var model = (MainViewModel)main.DataContext;
        var entry = OrbitCatalogParser.Parse(new CatalogSource { Id = "fixture", Name = "Synthetic Orbit" }, LocalOrbitCatalogTests.Feed()).Entries.First();
        entry.CoverUrl = fixture.Url("/bad-cover"); entry.CoverFallbackUrl = fixture.Url("/cover");
        var artifact = entry.Artifacts[0]; artifact.FileName = artifact.Name = "fixture.pkg";
        artifact.Size = fixture.Bytes.Length; artifact.Sha256 = Convert.ToHexString(SHA256.HashData(fixture.Bytes)); artifact.BrowserUrl = fixture.Url("/popup-file");
        var confirmations = 0;
        var browser = new BrowserDownloadWindow(entry, DownloadMirror.ForEntry(entry)[0], model.BrowserDownloads, Path.Combine(root, "profile"), message =>
        {
            confirmations++;
            Check(message.StartsWith("Accept this download"), "popup prompts must not interrupt the provider page");
            return true;
        }) { ShowActivated = false };
        try
        {
            model.Entries.Add(entry); main.Show();
            await Wait(() => Descendants<CoverThumbnail>(main).Any(t => t.Url == entry.CoverUrl && t.FallbackUrl == entry.CoverFallbackUrl && ((Image)t.Children[1]).Source is BitmapSource));
            browser.Show(); await browser.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Wait(async () => await browser.Browser.ExecuteScriptAsync("!!document.getElementById('download')") == "true");
            Check(browser.BlockPopups.IsChecked == true, "popup blocking enabled by default");
            await browser.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            await Wait(() => model.Downloads.Count == 1 && model.Downloads[0].State == "Downloaded" && model.Library.Count == 1);
            Check(confirmations == 0, $"exact selected filename is admitted without a confirmation prompt (actual: {confirmations})");
            Check(browser.PopupStatus.Text.StartsWith("Blocked "), "ad popup is reported as blocked: " + browser.PopupStatus.Text);
            Check(!fixture.Requests.ContainsKey("/popup-ad"), "blocked ad popup is never requested");
            Check(File.ReadAllBytes(model.Downloads[0].Destination).SequenceEqual(fixture.Bytes) && model.Library.Count == 1, "normal native file offer still completes with verified bytes and library registration");
            Check(model.Downloads[0].EntryId == entry.Id && model.Downloads[0].SourceId == entry.SourceId &&
                model.Downloads[0].CoverUrl == entry.CoverUrl && model.Downloads[0].CoverFallbackUrl == entry.CoverFallbackUrl &&
                model.Library[0].CatalogEntryId == entry.Id && model.Library[0].SourceId == entry.SourceId &&
                model.Library[0].CoverUrl == entry.CoverUrl && model.Library[0].CoverFallbackUrl == entry.CoverFallbackUrl,
                "selected catalogue identity and artwork carry from admission through library registration");
            var saved = store.Load();
            Check(saved.Downloads.Single().CoverUrl == entry.CoverUrl && saved.Library.Single().CoverFallbackUrl == entry.CoverFallbackUrl,
                "download and library artwork persist independently of catalogue availability");
            foreach (var header in new[] { "Downloads" })
            {
                var tab = Descendants<TabControl>(main).First().Items.Cast<TabItem>().Single(t => t.Header as string == header);
                tab.IsSelected = true;
                await Wait(() => Descendants<CoverThumbnail>(main).Any(t => t.IsVisible && t.Url == entry.CoverUrl && t.FallbackUrl == entry.CoverFallbackUrl && ((Image)t.Children[1]).Source is BitmapSource));
            }
            Check(model.LibraryView.IsEmpty, "nonrunnable download metadata stays outside My Library");
            Console.WriteLine("PASS displayed artwork, explicit fallback, filtering, Downloads-only nonrunnable content and popup blocking with verified native file transfer");
        }
        finally
        {
            browser.Close(); main.Close();
            for (var n = 0; n < 30; n++)
                try { Directory.Delete(root, true); break; } catch (IOException) { await Task.Delay(200); } catch (UnauthorizedAccessException) { await Task.Delay(200); }
            if (Directory.Exists(root)) Console.WriteLine("Presentation test profile pending process exit cleanup: " + root);
        }
    }
    private static Task Wait(Func<bool> condition) => Wait(() => Task.FromResult(condition()));
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var n = 0; n < VisualTreeHelper.GetChildrenCount(node); n++)
        {
            var child = VisualTreeHelper.GetChild(node, n);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Presentation fixture did not finish."); await Task.Delay(40); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
