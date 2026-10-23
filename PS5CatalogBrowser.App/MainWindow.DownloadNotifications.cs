using System.Windows;
using System.Windows.Threading;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    private readonly DispatcherTimer downloadToastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private void ConfigureDownloadNotifications()
    {
        model.DownloadAdded += ShowDownloadAdded;
        downloadToastTimer.Tick += (_, _) => { NativeDownloadToast.Visibility = Visibility.Collapsed; downloadToastTimer.Stop(); };
        Closed += (_, _) => { model.DownloadAdded -= ShowDownloadAdded; downloadToastTimer.Stop(); };
    }
    private void ShowDownloadAdded(DownloadJob job)
    {
        if (windowClosed) return;
        if (OrbitSurface.Visibility == Visibility.Visible) orbitFrontend?.ShowDownloadToast(job.Title);
        else
        {
            NativeDownloadToastText.Text = "Download added: " + job.Title;
            NativeDownloadToast.Visibility = Visibility.Visible;
            downloadToastTimer.Stop(); downloadToastTimer.Start();
        }
    }
}
