using System.Diagnostics;

namespace PS5CatalogBrowser.Core;

public sealed class KytyLauncher
{
    private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
    public bool HasActive { get { lock (active) return active.Count > 0; } }
    public bool IsActiveFor(string root) { lock (active) return active.Any(path => ImageFileSystem.SamePath(root, path) || ImageFileSystem.IsWithin(root, path)); }
    public static ProcessStartInfo BuildStartInfo(string executable, string gamePath, bool zarConfirmed = false, KytyArgumentSettings? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Choose kyty_emulator.exe in Windows Settings before launching a game.");
        if (string.IsNullOrWhiteSpace(gamePath)) throw new InvalidOperationException("This library entry has no game path. Reinspect it before launching.");
        executable = Path.GetFullPath(executable);
        gamePath = Path.GetFullPath(gamePath);
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("kyty_emulator.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select an existing kyty_emulator.exe.", executable);
        var classification = FileInspection.Classify(gamePath);
        if (classification is not ("Ready for launch attempt" or "Direct ELF launch attempt") &&
            !(zarConfirmed && Path.GetExtension(gamePath).Equals(".zar", StringComparison.OrdinalIgnoreCase) && File.Exists(gamePath)))
            throw new InvalidOperationException("Content is missing or not structurally eligible for a KytyPS5 launch attempt: " + classification);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        info.ArgumentList.Add("--game");
        info.ArgumentList.Add(gamePath);
        foreach (var argument in KytyArguments.Build(arguments)) info.ArgumentList.Add(argument);
        return info;
    }

    public LoggedLaunch Launch(string executable, string gamePath, string logDirectory, Action<string> logReady, KytyArgumentSettings? arguments = null, EmulatorBackend backend = EmulatorBackend.KytyPS5, bool sharpEmuFullscreen = false)
    {
        var info = backend switch
        {
            EmulatorBackend.KytyPS5 => BuildStartInfo(executable, gamePath, arguments: arguments),
            EmulatorBackend.SharpEmu => SharpEmuLauncher.BuildStartInfo(executable, gamePath, sharpEmuFullscreen),
            _ => throw new InvalidOperationException("The saved emulator is unsupported. Hold the game cover to choose an emulator.")
        };
        lock (active)
            if (!active.Add(gamePath)) throw new InvalidOperationException("A launch attempt is already active for this item.");
        IDisposable? lease = null;
        try
        {
            if (backend == EmulatorBackend.SharpEmu && ImageFileSystem.IsWithin(Path.Combine(SharpEmuUpdater.DefaultRoot, "installed"), executable))
                lease = SharpEmuUpdater.AcquireRuntimeLease(SharpEmuUpdater.DefaultRoot, updating: false);
            var launch = KytyLaunchLog.Start(info, logDirectory, logReady, backend.ToString());
            var ownedLease = lease;
            return new(launch.LogId, CompleteAsync());
            async Task<int> CompleteAsync()
            {
                try { return await launch.Completion.ConfigureAwait(false); }
                finally { ownedLease?.Dispose(); lock (active) active.Remove(gamePath); }
            }
        }
        catch { lease?.Dispose(); lock (active) active.Remove(gamePath); throw; }
    }
}
