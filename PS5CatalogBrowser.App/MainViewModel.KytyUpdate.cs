using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    internal Func<Task<string>> LookupKytyRelease { get; set; } = FetchKytyReleaseAsync;
    internal Func<ProcessStartInfo, Action<string>, Task<int>> RunKytyUpdater { get; set; } = RunKytyUpdaterAsync;
    private string repositoryKytyVersion = "", kytyUpdateStatus = "Check for updates to compare the installed build with the latest release.", kytyUpdateOutput = "";
    public bool IsCheckingKytyVersion { get; private set; }
    public bool CanCheckKytyVersion => !IsUpdatingSharpEmu && !IsCheckingKytyVersion && !IsUpdatingKyty;
    public bool CanEditKytyPath => !IsUpdatingKyty;
    public string InstalledKytyVersion => CurrentKytyVersion.Length == 0 ? "Not detected" : CurrentKytyVersion;
    public string RepositoryKytyVersion => repositoryKytyVersion.Length == 0 ? "Not checked" : repositoryKytyVersion;
    public string KytyUpdateStatus => kytyUpdateStatus;
    public string KytyUpdateOutput => kytyUpdateOutput;
    private string KytyUpdateScript => Path.Combine(AppContext.BaseDirectory, "KytyUpdate", "Update-KytyPS5.ps1");
    private bool ManagedKytyRuntime => Path.GetFileName(Settings.EmulatorPath).Equals("kyty_emulator.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(Settings.EmulatorPath)), "KytyPS5", StringComparison.OrdinalIgnoreCase);
    public bool CanUpdateKyty => CanCheckKytyVersion && repositoryKytyVersion.Length > 0 && File.Exists(Settings.EmulatorPath) &&
        ManagedKytyRuntime && File.Exists(KytyUpdateScript) && !KytyVersion.MatchesRelease(CurrentKytyVersion, repositoryKytyVersion);
    private string DescribeKytyVersions() => KytyVersion.MatchesRelease(CurrentKytyVersion, repositoryKytyVersion) ? "The installed build matches the latest release. The custom menu patch suffix is expected." :
        !ManagedKytyRuntime || !File.Exists(Settings.EmulatorPath) ? "Select the managed Tools/KytyPS5/kyty_emulator.exe to enable updates." :
        !File.Exists(KytyUpdateScript) ? "The KytyPS5 update helper is missing. Rebuild or reinstall OrbitX." :
        "Versions differ. Update builds the latest official release with the custom menu patch. Close KytyPS5 before updating.";
    private void RefreshKytyUpdateAvailability()
    {
        if (!IsCheckingKytyVersion && !IsUpdatingKyty && repositoryKytyVersion.Length > 0) kytyUpdateStatus = DescribeKytyVersions();
        NotifyKytyUpdateState();
    }

    private void NotifyKytyUpdateState()
    {
        Changed(nameof(InstalledKytyVersion)); Changed(nameof(RepositoryKytyVersion)); Changed(nameof(KytyUpdateStatus)); Changed(nameof(KytyUpdateOutput));
        Changed(nameof(IsCheckingKytyVersion)); Changed(nameof(IsUpdatingKyty)); Changed(nameof(CanCheckKytyVersion)); Changed(nameof(CanEditKytyPath));
        Changed(nameof(CanUpdateKyty)); Changed(nameof(CanLaunchSelected)); NotifySharpEmu();
    }
    internal async Task CheckForKytyUpdateAsync()
    {
        if (!CanCheckKytyVersion) return;
        IsCheckingKytyVersion = true; repositoryKytyVersion = "";
        kytyUpdateStatus = "Checking the latest official KytyPS5 release..."; RefreshKytyVersion(); NotifyKytyUpdateState();
        try
        {
            var release = await LookupKytyRelease().ConfigureAwait(false);
            if (!KytyVersion.IsReleaseTag(release)) throw new InvalidDataException("The repository returned an unrecognized release tag. Try Check for updates again later.");
            await UpdateUiAsync(() =>
            {
                repositoryKytyVersion = release;
                kytyUpdateStatus = DescribeKytyVersions();
            });
        }
        catch (Exception ex) { await UpdateUiAsync(() => kytyUpdateStatus = "Could not check the repository version: " + ex.Message); }
        finally { await UpdateUiAsync(() => { IsCheckingKytyVersion = false; NotifyKytyUpdateState(); }); }
    }

    public async Task UpdateKytyAsync()
    {
        if (!CanUpdateKyty) return;
        IsUpdatingKyty = true; kytyUpdateOutput = "";
        kytyUpdateStatus = "Starting KytyPS5 update. Games cannot launch until it finishes."; NotifyKytyUpdateState();
        var output = new StringBuilder(); var outputGate = new object();
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Settings.EmulatorPath)! };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", KytyUpdateScript,
                "-RuntimePath", Settings.EmulatorPath, "-InstalledVersion", CurrentKytyVersion, "-Force" }) start.ArgumentList.Add(argument);
            var code = await RunKytyUpdater(start, line =>
            {
                string tail;
                lock (outputGate)
                {
                    output.AppendLine(line);
                    if (output.Length > 65536) output.Remove(0, output.Length - 65536);
                    tail = output.ToString();
                }
                _ = UpdateUiAsync(() =>
                {
                    kytyUpdateOutput = tail; Changed(nameof(KytyUpdateOutput));
                    if (line.StartsWith("KytyPS5:", StringComparison.Ordinal)) { kytyUpdateStatus = line; Changed(nameof(KytyUpdateStatus)); }
                });
            }).ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                lock (outputGate) kytyUpdateOutput = output.ToString();
                var updated = System.Text.RegularExpressions.Regex.Match(kytyUpdateOutput, @"KytyPS5: Updated to (KytyPS5-\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}) with");
                if (code == 0 && updated.Success) repositoryKytyVersion = updated.Groups[1].Value;
                RefreshKytyVersion();
                kytyUpdateStatus = code == 0 && KytyVersion.MatchesRelease(CurrentKytyVersion, repositoryKytyVersion) ? "KytyPS5 is updated to the latest release with the custom menu patch." :
                    "KytyPS5 update failed. Review Update output, correct the reported problem, then retry.";
                Status = kytyUpdateStatus;
            });
        }
        catch (Exception ex) { await UpdateUiAsync(() => { kytyUpdateStatus = "KytyPS5 update failed: " + ex.Message; Status = kytyUpdateStatus; }); }
        finally { await UpdateUiAsync(() => { IsUpdatingKyty = false; NotifyKytyUpdateState(); }); }
    }

    private static async Task<string> FetchKytyReleaseAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PS5-Catalog-Browser/1.0");
        var json = await client.GetStringAsync("https://api.github.com/repos/KytyPS5/KytyPS5/releases/latest").ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("tag_name").GetString() ?? "";
    }
    private static async Task<int> RunKytyUpdaterAsync(ProcessStartInfo info, Action<string> output)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the KytyPS5 updater.");
        async Task Drain(StreamReader stream) { while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line) output(line); }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        await process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }
    private async Task UpdateUiAsync(Action update)
    {
        if (disposed || ui.HasShutdownStarted || ui.HasShutdownFinished) return;
        try { await ui.InvokeAsync(() => { if (!disposed) update(); }); }
        catch (InvalidOperationException) when (ui.HasShutdownStarted || ui.HasShutdownFinished) { }
        catch (TaskCanceledException) when (ui.HasShutdownStarted || ui.HasShutdownFinished) { }
    }
}
