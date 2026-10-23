using System.Diagnostics;
using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    private SharpEmuInstallation? sharpInstallation;
    private string sharpUpdateStatus = "Check for updates to compare official main with the installed revision.", sharpOutput = "", sharpLog = "";
    public bool IsUpdatingSharpEmu { get; private set; }
    public bool IsCheckingSharpEmu { get; private set; }
    public bool SharpEmuFullscreen
    {
        get => Settings.SharpEmuFullscreen;
        set
        {
            var previous = Settings.SharpEmuFullscreen;
            if (previous == value) return;
            Settings.SharpEmuFullscreen = value;
            try { Save(); } catch { Settings.SharpEmuFullscreen = previous; throw; }
            Changed();
        }
    }
    public bool CanManageSharpEmu => !IsUpdatingSharpEmu && !IsCheckingSharpEmu && !IsUpdatingKyty;
    public bool CanBuildSharpEmu => CanManageSharpEmu && !launcher.HasActive && SharpEmuUpdater.IsRevision(Settings.SharpEmuAvailableRevision);
    public string SharpEmuPath
    {
        get => Settings.SharpEmuPath;
        set
        {
            if (!CanManageSharpEmu) throw new InvalidOperationException("Wait for the emulator operation to finish.");
            SharpEmuLauncher.ValidateExecutable(value);
            var previous = Settings.SharpEmuPath; Settings.SharpEmuPath = Path.GetFullPath(value);
            try { Save(); } catch { Settings.SharpEmuPath = previous; throw; }
            NotifySharpEmu();
        }
    }
    public string SharpEmuAvailability => File.Exists(SharpEmuPath) ? "Executable found" : "Not installed: Update/build the managed emulator or select SharpEmu.exe.";
    public string SharpEmuOwnership => ImageFileSystem.SamePath(SharpEmuPath, Path.Combine(SharpEmuUpdater.DefaultRoot, "installed", "SharpEmu.exe"))
        ? "Managed installation" : "External executable. Updates build the managed installation and preserve this selection.";
    public string SharpEmuInstalledVersion => sharpInstallation?.Version ?? "Managed patched build not installed";
    public string SharpEmuInstalledRevision => sharpInstallation?.UpstreamCommit ?? "Not recorded";
    public string SharpEmuRevisionLabel => sharpInstallation?.UpstreamCommit[..12] ?? "Not recorded";
    public string SharpEmuAvailableRevision => Settings.SharpEmuAvailableRevision.Length == 0 ? "Not checked" : Settings.SharpEmuAvailableRevision;
    public string SharpEmuCheckTime => Settings.SharpEmuLastCheck?.ToLocalTime().ToString("g") ?? "Never";
    public string SharpEmuUpdateTime => sharpInstallation?.InstalledAt.ToLocalTime().ToString("g") ?? "Never";
    public string SharpEmuF10Support => sharpInstallation?.F10Support ?? "Not verified";
    public string SharpEmuInstallDirectory => Path.Combine(SharpEmuUpdater.DefaultRoot, "installed");
    public string SharpEmuUpdateStatus => sharpUpdateStatus;
    public string SharpEmuUpdateOutput => sharpOutput;
    public string SharpEmuBuildLog
    {
        get
        {
            if (sharpLog.Length > 0) return sharpLog;
            if (Settings.SharpEmuLastBuildLog.Length > 0) return Settings.SharpEmuLastBuildLog;
            var directory = Path.Combine(SharpEmuUpdater.DefaultRoot, "logs");
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.log")
                .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                .MaxBy(File.GetLastWriteTimeUtc) ?? "" : "";
        }
    }
    internal Func<Action<string>, SharpEmuUpdater> CreateSharpUpdater { get; set; } = output =>
        new(SharpEmuUpdater.DefaultRoot, Path.Combine(AppContext.BaseDirectory, "SharpEmuUpdate", "f10-menu.patch"), output);

    private void LoadSharpEmuInstallation()
    {
        try { sharpInstallation = SharpEmuUpdater.ReadInstallation(SharpEmuInstallDirectory); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { sharpUpdateStatus = "Could not read managed SharpEmu metadata: " + ex.Message; }
    }
    private void NotifySharpEmu()
    {
        foreach (var name in new[] { nameof(SharpEmuPath), nameof(SharpEmuOwnership), nameof(SharpEmuAvailability), nameof(CanManageSharpEmu),
            nameof(CanBuildSharpEmu), nameof(SharpEmuInstalledVersion), nameof(SharpEmuInstalledRevision), nameof(SharpEmuRevisionLabel),
            nameof(SharpEmuAvailableRevision), nameof(SharpEmuCheckTime), nameof(SharpEmuUpdateTime), nameof(SharpEmuF10Support),
            nameof(SharpEmuUpdateStatus), nameof(SharpEmuUpdateOutput), nameof(SharpEmuBuildLog), nameof(CanLaunchSelected) }) Changed(name);
    }
    internal async Task CheckSharpEmuAsync()
    {
        if (!CanManageSharpEmu) return;
        IsCheckingSharpEmu = true; sharpUpdateStatus = "Checking official upstream main..."; NotifySharpEmu();
        try
        {
            var updater = CreateSharpUpdater(_ => { });
            var revision = await Task.Run(updater.CheckAsync).ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                sharpInstallation = SharpEmuUpdater.ReadInstallation(SharpEmuInstallDirectory);
                Settings.SharpEmuAvailableRevision = revision; Settings.SharpEmuLastCheck = DateTimeOffset.UtcNow; Save();
                sharpUpdateStatus = updater.Status;
            });
        }
        catch (Exception ex) { await UpdateUiAsync(() => { sharpUpdateStatus = "Could not check SharpEmu: " + ex.Message; Settings.SharpEmuAvailableRevision = ""; }); }
        finally { await UpdateUiAsync(() => { IsCheckingSharpEmu = false; NotifySharpEmu(); }); }
    }
    public async Task UpdateSharpEmuAsync()
    {
        if (!CanBuildSharpEmu) return;
        IsUpdatingSharpEmu = true; NotifyKytyUpdateState(); sharpOutput = ""; sharpUpdateStatus = "Starting managed SharpEmu update..."; NotifySharpEmu();
        var output = new System.Text.StringBuilder(); var gate = new object();
        var updater = CreateSharpUpdater(line =>
        {
            string tail;
            lock (gate) { output.AppendLine(line); if (output.Length > 65536) output.Remove(0, output.Length - 65536); tail = output.ToString(); }
            _ = UpdateUiAsync(() => { sharpOutput = tail; if (line.StartsWith("SharpEmu:", StringComparison.Ordinal)) sharpUpdateStatus = line; NotifySharpEmu(); });
        });
        try
        {
            var revision = Settings.SharpEmuAvailableRevision;
            var installed = await Task.Run(() => updater.UpdateAsync(revision)).ConfigureAwait(false);
            await UpdateUiAsync(() => { sharpInstallation = installed; sharpUpdateStatus = updater.Status; });
        }
        catch (Exception ex) { await UpdateUiAsync(() => sharpUpdateStatus = updater.Status + ": " + ex.Message); }
        finally { await UpdateUiAsync(() => { sharpLog = updater.LogPath; if (sharpLog.Length > 0) { Settings.SharpEmuLastBuildLog = sharpLog; try { Save(); } catch (Exception ex) { sharpUpdateStatus += " Settings save failed: " + ex.Message; } } IsUpdatingSharpEmu = false; NotifyKytyUpdateState(); Status = sharpUpdateStatus; NotifySharpEmu(); }); }
    }
}
