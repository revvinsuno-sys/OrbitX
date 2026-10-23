using System.Diagnostics;
using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    public bool CanManageShadPS4 => !launcher.HasActive;
    public string ShadPS4Path
    {
        get => Settings.ShadPS4Path;
        set
        {
            if (!CanManageShadPS4) throw new InvalidOperationException("Wait for the active game to close before changing the shadPS4 executable.");
            ShadPS4Launcher.ValidateExecutable(value);
            var previous = Settings.ShadPS4Path;
            Settings.ShadPS4Path = Path.GetFullPath(value);
            try { Save(); }
            catch { Settings.ShadPS4Path = previous; throw; }
            NotifyShadPS4();
        }
    }
    public bool ShadPS4Fullscreen
    {
        get => Settings.ShadPS4Fullscreen;
        set
        {
            var previous = Settings.ShadPS4Fullscreen;
            if (previous == value) return;
            Settings.ShadPS4Fullscreen = value;
            try { Save(); }
            catch { Settings.ShadPS4Fullscreen = previous; throw; }
            Changed();
        }
    }
    public string ShadPS4Availability => File.Exists(ShadPS4Path) && Path.GetFileName(ShadPS4Path).Equals("shadPS4.exe", StringComparison.OrdinalIgnoreCase)
        ? "Executable found" : "Select shadPS4.exe";

    public void TestShadPS4()
    {
        if (!CanManageShadPS4) throw new InvalidOperationException("Wait for the active game to close before testing shadPS4.");
        using var process = Process.Start(ShadPS4Launcher.BuildHelpStartInfo(ShadPS4Path))
            ?? throw new IOException("Could not start shadPS4.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("shadPS4 did not return its command help within ten seconds.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0 || !output.Result.Contains("shadPS4 Emulator CLI", StringComparison.Ordinal))
            throw new IOException("shadPS4 command check failed: " + error.Result.Trim());
        Status = "shadPS4 command line verified. PS4 firmware modules and a game dump are configured separately.";
        NotifyShadPS4();
    }

    public void NotifyShadPS4()
    {
        Changed(nameof(ShadPS4Path)); Changed(nameof(ShadPS4Fullscreen)); Changed(nameof(ShadPS4Availability));
        Changed(nameof(CanManageShadPS4)); Changed(nameof(CanLaunchSelected));
    }
}
