using System.Diagnostics;
using PS5CatalogBrowser.Core;

internal static class OSFMountDependencyTests
{
    public static Task RunAsync()
    {
        var console = OSFMountDependency.Detect();
        if (!OperatingSystem.IsWindows() || console.Length == 0)
        {
            Console.WriteLine("SKIP installed OSFMount version validation: dependency unavailable.");
            return Task.CompletedTask;
        }
        var executable = Path.ChangeExtension(console, ".exe");
        var version = FileVersionInfo.GetVersionInfo(executable);
        if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) < OSFMountDependency.MinimumVersion)
        {
            try { OSFMountDependency.Validate(console); }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("OSFMount 3.3.1000 or later is required")) { return Task.CompletedTask; }
            throw new Exception("Outdated companion executable was accepted.");
        }
        var result = OSFMountDependency.Validate(console);
        if (!result.StartsWith("OSFMount " + version.FileVersion + " — signatures verified;"))
            throw new Exception("OSFMount validation must report the signed companion executable's version.");
        Console.WriteLine("PASS installed OSFMount console and companion signatures, executable version " + version.FileVersion + " (no mount/elevation)");
        return Task.CompletedTask;
    }
}
