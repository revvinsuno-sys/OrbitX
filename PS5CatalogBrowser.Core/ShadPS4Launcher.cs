using System.Diagnostics;

namespace PS5CatalogBrowser.Core;

public static class ShadPS4Launcher
{
    public const string DefaultRoot = @"D:\Games\PS5\Tools\shadPS4";
    public static string DefaultExecutable => Path.Combine(DefaultRoot, "installed", "shadPS4.exe");

    public static void ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("shadPS4.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select an existing Windows shadPS4.exe in Emulators settings.", executable);
        ImageFileSystem.EnsureNoLinks(executable);
        using var input = File.OpenRead(executable);
        using var reader = new BinaryReader(input);
        if (input.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new IOException("shadPS4 must be a Windows x64 executable.");
        input.Position = 0x3c; var pe = reader.ReadUInt32();
        if (pe > input.Length - 6) throw new IOException("The shadPS4 executable has an invalid PE header.");
        input.Position = pe;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new IOException("Select the Windows x64 shadPS4 build.");
    }

    public static ProcessStartInfo BuildHelpStartInfo(string executable)
    {
        ValidateExecutable(executable);
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        info.ArgumentList.Add("--help");
        return info;
    }

    public static ProcessStartInfo BuildStartInfo(string executable, string gamePath, bool fullscreen = false)
    {
        ValidateExecutable(executable);
        var eboot = Directory.Exists(gamePath) ? Path.Combine(gamePath, "eboot.bin") : gamePath;
        if (!File.Exists(eboot) || !(Path.GetFileName(eboot).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(eboot).Equals(".elf", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("shadPS4 requires a PS4 eboot.bin or ELF executable. Select the extracted PS4 game folder or its launchable executable.");
        ImageFileSystem.EnsureNoLinks(eboot);
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        info.ArgumentList.Add("--game");
        info.ArgumentList.Add(Path.GetFullPath(eboot));
        info.ArgumentList.Add("--fullscreen");
        info.ArgumentList.Add(fullscreen ? "true" : "false");
        return info;
    }
}
