using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows.Controls;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class RunnableContentUiTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5RunnableUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
            var installed = Path.Combine(root, "installed");
            var archive = Path.Combine(downloads, "archive.zip"); File.WriteAllText(archive, "not a runnable file");
            var sibling = Path.Combine(root, "installed-other"); Directory.CreateDirectory(sibling);
            var outside = Path.Combine(sibling, "other.elf"); File.WriteAllBytes(outside, SyntheticExFat.TestExecutable());
            store.Save(new() { Sources = [], DownloadRoot = downloads, InstalledGamesRoot = installed,
                Library = [new() { ContentPath = archive }, new() { ContentPath = outside }] });
            window = new MainWindow(store) { ShowActivated = false }; window.Show();
            var model = (MainViewModel)window.DataContext;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (model.InstallationAvailability is "Checking OSFMount…" or "Test OSFMount availability in Settings" || model.IsMovingContent)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(50); }
            Check(model.Library.Count == 2 && model.LibraryView.IsEmpty, "archives and sibling-prefix directories are hidden without losing metadata");
            var external = Path.Combine(root, "game.elf"); var bytes = SyntheticExFat.TestExecutable(); File.WriteAllBytes(external, bytes);
            var artifact = new CatalogArtifact { Id = "runnable", Url = "https://fixture.invalid/game.elf", FileName = "game.elf", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            var entry = new CatalogEntry { Id = "synthetic", SourceId = "fixture", Title = "Runnable fixture", Artifacts = [artifact] };
            await model.BrowserDownloads.ImportAsync(entry, DownloadMirror.ForEntry(entry).Single(), external);
            Check(model.Downloads.Count == 0 && model.Settings.Downloads.Single().State == "Installed", "completed runnable import leaves Downloads");
            var item = model.LibraryView.Cast<LocalLibraryEntry>().Single();
            Check(item.ContentPath == Path.Combine(installed, "game.elf") && File.Exists(external), "library points to installed executable and external import source is preserved");
            var tabs = ((Grid)window.Content).Children.OfType<TabControl>().Single();
            tabs.Items.OfType<TabItem>().Single(tab => tab.Header.ToString() == "My Library").IsSelected = true;
            window.UpdateLayout();
            Check(((ListBox)window.FindName("LibraryList")).Items.Count == 1, "My Library renders only the installed record");
            model.InstalledGamesRoot = sibling;
            Check(model.LibraryView.Cast<LocalLibraryEntry>().Single().ContentPath == outside, "changing configured Installed root refreshes visible membership");
            model.InstalledGamesRoot = installed;
            Check(model.LibraryView.Cast<LocalLibraryEntry>().Single().Id == item.Id, "changing roots never deletes retained records");
            window.Close(); window = null;
            using var restarted = new MainViewModel(store);
            Check(restarted.Downloads.Count == 0 && restarted.LibraryView.Cast<LocalLibraryEntry>().Single().ContentPath == item.ContentPath,
                "installed-only library and hidden moved history survive restart");
            await NativeCompletionAsync(restarted, root, bytes);
            await ExtractionFeedbackAsync(root);
            await CloseDuringVerificationAsync(root);
            Console.WriteLine("PASS runnable completion/import, installed-only My Library, source preservation, root changes and restart");
        }
        finally
        {
            window?.Close();
            for (var attempt = 0; attempt < 40 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { await Task.Delay(250); }
                catch (UnauthorizedAccessException) { await Task.Delay(250); }
            }
            if (Directory.Exists(root)) throw new IOException("Runnable UI fixture remains in use: " + root);
        }
    }
    private static async Task CloseDuringVerificationAsync(string root)
    {
        var local = Path.Combine(root, "close-verification");
        var source = Path.Combine(local, "downloads", "game"); Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "eboot.bin"), new byte[8 * 1024 * 1024]);
        var store = new SettingsStore(Path.Combine(local, "settings"));
        store.Save(new() { Sources = [], DownloadRoot = Path.GetDirectoryName(source)!, InstalledGamesRoot = Path.Combine(local, "installed"),
            Library = [new() { ContentPath = source, Extraction = "Extracted", Title = "Close verification fixture" }] });
        var window = new MainWindow(store) { ShowActivated = false };
        var model = (MainViewModel)window.DataContext;
        var closed = new TaskCompletionSource(); var requested = false;
        window.Closed += (_, _) => closed.TrySetResult();
        model.PropertyChanged += (_, args) =>
        {
            if (requested || args.PropertyName != nameof(MainViewModel.ExtractionFeedback) || !model.ContentMoveProgressText.Contains("Hashing extracted files")) return;
            requested = true;
            Check(model.ActiveContentMove?.Title == "Close verification fixture", "verification progress retains exact item identity");
            window.Close();
        };
        try
        {
            window.Show(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(requested && !model.IsMovingContent && Directory.Exists(source) && store.Load().Library.Single().ContentMove is null,
                "Close cancels verification, releases ownership and closes automatically with source preserved");
            Console.WriteLine("PASS Close during active verification cancels safely and closes without a second click");
        }
        finally { if (!closed.Task.IsCompleted) { await model.StopContentMoveAsync(); window.Close(); } }
    }
    private static async Task ExtractionFeedbackAsync(string root)
    {
        var store = new SettingsStore(Path.Combine(root, "extraction-settings"));
        store.Save(new() { Sources = [], DownloadRoot = Path.Combine(root, "zip-downloads"), InstalledGamesRoot = Path.Combine(root, "zip-installed") });
        using (var model = new MainViewModel(store))
        {
            foreach (var valid in new[] { false, true })
            {
                var zip = Path.Combine(root, valid ? "valid.zip" : "unsupported.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    using var output = archive.CreateEntry((valid ? "valid-game" : "unsupported-game") + "/eboot.bin").Open();
                    output.Write(valid ? SyntheticExFat.TestExecutable() : new byte[64]);
                }
                var bytes = File.ReadAllBytes(zip);
                var artifact = new CatalogArtifact { Id = zip, Url = "https://fixture.invalid/" + Path.GetFileName(zip), FileName = Path.GetFileName(zip), Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
                var entry = new CatalogEntry { Id = zip, Title = Path.GetFileName(zip), Artifacts = [artifact] };
                await model.BrowserDownloads.ImportAsync(entry, DownloadMirror.ForEntry(entry).Single(), zip);
                model.SelectedJob = model.Downloads.Last();
                Check(model.CanRunDownloadAction && model.DownloadActionLabel == "Extract and install", "ZIP has a single clear next action");
                var extraction = model.ExtractSelectedDownloadAsync();
                Check(model.IsExtracting && !model.CanRunDownloadAction && model.ExtractionFeedback.Contains("Extracting"), "extraction immediately reports busy and blocks duplicate actions");
                await extraction;
                Check(!model.IsExtracting && File.Exists(zip), "extraction finishes and preserves the original archive");
                Check(model.ExtractionFeedback.Contains("now in My Library") && model.LibraryView.Cast<object>().Count() == (valid ? 2 : 1), "extracted game installs independently of Kyty structural compatibility: " + model.ExtractionFeedback + "; library=" + model.LibraryView.Cast<object>().Count());
            }
            foreach (var extension in new[] { ".ffpkg", ".7z" })
            {
                model.SelectedJob = new() { State = "Downloaded", Destination = Path.Combine(root, "unsupported" + extension) };
                Check(!model.ShowDownloadAction && !model.CanRunDownloadAction &&
                    (model.DownloadExplanation.Contains("not supported") || model.DownloadExplanation.Contains("conversion")), "unsupported formats explain their limitation without a disabled install button");
            }
            var rar = Path.Combine(root, "synthetic.rar"); File.WriteAllBytes(rar, Convert.FromBase64String(PackageArchiveTests.Rar5));
            await Import(rar);
            Check(model.CanExtractDownload && model.DownloadActionLabel == "Extract and install", "RAR extraction action available");
            await model.ExtractSelectedDownloadAsync();
            Check(model.ExtractionFeedback.Contains("now in My Library") && model.LibraryView.Cast<object>().Count() == 3 && File.Exists(rar), "RAR extracted game installed and source retained");
            model.OSFMountPath = ""; await model.TestInstallationAsync();
            foreach (var ps5 in new[] { false, true })
            {
                var pkg = Path.Combine(root, ps5 ? "ps5.pkg" : "ps4.pkg"); PackageArchiveTests.BuildPackage(pkg, ps5);
                await Import(pkg);
                Check(model.CanInstallDownload && model.DownloadActionLabel == "Install", "PKG installs without OSFMount");
                var before = model.LibraryView.Cast<object>().Count();
                await model.InstallImageAsync(model.SelectedJob!.Destination, confirmation => Task.FromResult<InstallationChoice?>(new(confirmation.FolderName + (ps5 ? " PS5" : " PS4"), false)));
                Check(model.LibraryView.Cast<object>().Count() == before + 1 && File.Exists(pkg), "PKG installed, source preserved and library refreshed");
            }
            var launchItem = model.LibraryView.Cast<LocalLibraryEntry>().First();
            var badEmulator = Path.Combine(root, "kyty_emulator.exe"); File.WriteAllText(badEmulator, "synthetic invalid executable"); model.EmulatorPath = badEmulator;
            var content = launchItem.ContentPath;
            try { model.Launch(launchItem); throw new Exception("Invalid emulator unexpectedly launched"); }
            catch (System.ComponentModel.Win32Exception) { }
            Check(Directory.Exists(content) && model.LibraryView.Contains(launchItem), "failed Kyty process launch retains installed content");
            async Task Import(string path)
            {
                var bytes = File.ReadAllBytes(path);
                var artifact = new CatalogArtifact { Id = path, Url = "https://fixture.invalid/" + Path.GetFileName(path), FileName = Path.GetFileName(path), Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
                var entry = new CatalogEntry { Id = path, Title = Path.GetFileName(path), Artifacts = [artifact] };
                await model.BrowserDownloads.ImportAsync(entry, DownloadMirror.ForEntry(entry).Single(), path);
                model.SelectedJob = model.Downloads.Last();
            }
        }
        using var restarted = new MainViewModel(store);
        Check(restarted.LibraryView.Cast<object>().Count() == 5, "RAR/PKG installation persists across restart");
        Console.WriteLine("PASS ZIP/RAR progress, Kyty-incompatible content installation, PS4/PS5 PKG actions without OSFMount and persisted installed library");
    }
    private static async Task NativeCompletionAsync(MainViewModel model, string root, byte[] bytes)
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var url = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start();
        var serve = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            request.Response.ContentType = "application/octet-stream";
            request.Response.Headers["Content-Disposition"] = "attachment; filename=\"native.elf\"";
            request.Response.ContentLength64 = bytes.Length;
            await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
        });
        using var landing = new BrowserFixtures();
        var artifact = new CatalogArtifact { Id = "native", Url = url, FileName = "native.elf", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        var entry = new CatalogEntry { Id = "native", Title = "Native runnable fixture", Artifacts = [artifact] };
        var mirror = new DownloadMirror { Artifact = artifact, Url = landing.Url("/landing?target=" + Uri.EscapeDataString(url)), ExpectedFileName = artifact.FileName };
        var browser = new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads, Path.Combine(root, "profile"), _ => true);
        try
        {
            browser.Show(); await browser.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (await browser.Browser.ExecuteScriptAsync("!!document.getElementById('download')") != "true")
            { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(50); }
            await browser.Browser.ExecuteScriptAsync("document.getElementById('download').click()");
            while (model.Settings.Downloads.Count < 2 || model.BrowserDownloads.HasActive)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(50); }
            await serve;
            var job = model.Settings.Downloads.Last();
            Check(job.State == "Installed" && job.Error == "" && job.IsCleared && model.Downloads.Count == 0 &&
                File.ReadAllBytes(Path.Combine(model.InstalledGamesRoot, "native.elf")).SequenceEqual(bytes) && model.LibraryView.Cast<object>().Count() == 2,
                "real WebView2 completion relocates ELF, releases native ownership, cleans staging and updates the library");
        }
        finally { browser.Close(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
