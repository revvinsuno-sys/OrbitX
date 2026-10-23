using System.Diagnostics;

namespace PS5CatalogBrowser.Core;

public enum EmulatorBackend { KytyPS5, SharpEmu, ShadPS4 }

public static class SharpEmuLauncher
{
    public static void ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("SharpEmu.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select an existing SharpEmu.exe in Settings, or hold the cover to choose KytyPS5.", executable);
        ImageFileSystem.EnsureNoLinks(executable);
        using var input = File.OpenRead(executable);
        using var reader = new BinaryReader(input);
        if (input.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new IOException("SharpEmu must be a Windows x64 executable.");
        input.Position = 0x3c; var pe = reader.ReadUInt32();
        if (pe > input.Length - 6) throw new IOException("The SharpEmu executable has an invalid PE header.");
        input.Position = pe;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new IOException("Select the Windows x64 SharpEmu build.");
    }
    public static ProcessStartInfo BuildStartInfo(string executable, string gamePath, bool fullscreen = false)
    {
        ValidateExecutable(executable);
        var eboot = Directory.Exists(gamePath) ? Path.Combine(gamePath, "eboot.bin") : gamePath;
        if (!Path.GetFileName(eboot).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) || !File.Exists(eboot))
            throw new IOException("SharpEmu requires eboot.bin at the selected game root. Reinspect this installation or choose a game folder.");
        ImageFileSystem.EnsureNoLinks(eboot);
        if (FileInspection.Classify(Directory.GetParent(Path.GetFullPath(eboot))!.FullName) != "Ready for launch attempt")
            throw new IOException("The selected eboot.bin is invalid or unsupported. Reinspect the installation; game files were not changed.");
        // SharpEmu owns ELF/SELF compatibility checks. The Kyty-specific validator
        // rejects layouts that SharpEmu may support, so only validate the selected path here.
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        info.ArgumentList.Add(Path.GetFullPath(eboot));
        // Verified against b6347f9: --log-to-file in upstream README is obsolete.
        info.ArgumentList.Add("--log-file");
        info.ArgumentList.Add(fullscreen ? "--window-mode=fullscreen" : "--window-mode=windowed");
        return info;
    }
}
