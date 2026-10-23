using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PS5CatalogBrowser.Core;

public static class OSFMountDependency
{
    public const string DownloadUrl = "https://www.osforensics.com/tools/mount-disk-images.html";
    public static readonly Version MinimumVersion = new(3, 3, 1000);
    public static string Detect()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OSFMount"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OSFMount")
        };
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OSFMount_is1");
            if (uninstall?.GetValue("InstallLocation") is string location && location.Length > 0) roots.Add(location);
        }
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(path => path.Length > 0));
        foreach (var root in roots)
            foreach (var name in new[] { "OSFMount.com" })
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) return path;
            }
        return "";
    }
    public static string Validate(string executable, bool companion = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OSFMount requires Windows.");
        if (executable.Length == 0 || !File.Exists(executable)) throw new FileNotFoundException("Install OSFMount 3.3.1000 or later from PassMark, then choose its OSFMount.com executable in Settings.");
        executable = Path.GetFullPath(executable); ImageFileSystem.EnsureNoLinks(executable);
        if (!Path.GetFileName(executable).Equals(companion ? "OSFMount.exe" : "OSFMount.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the official OSFMount.com console executable.");
        if (!new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Any(root => root.Length > 0 && ImageFileSystem.IsWithin(root, executable)))
            throw new InvalidDataException("Use a system-wide OSFMount installation under Program Files. Elevation from a user-writable tools/download folder is not supported.");
        var version = FileVersionInfo.GetVersionInfo(executable);
        // The signed console launcher has no version resource; the companion executable supplies it.
        if (companion && new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) < MinimumVersion)
            throw new InvalidDataException("OSFMount 3.3.1000 or later is required; update the installed driver and executable together.");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = executable };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1, File = pointer, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new InvalidDataException("OSFMount's Authenticode signature could not be validated. Select an official installed binary.");
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executable));
#pragma warning restore SYSLIB0057
            if (!certificate.GetNameInfo(X509NameType.SimpleName, false).Equals("PassMark Software Pty Ltd", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected binary is not signed by PassMark Software.");
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
        if (!companion) return Validate(Path.ChangeExtension(executable, ".exe"), true);
        return "OSFMount " + version.FileVersion + " — signatures verified; driver and temporary administrator approval are checked when mounting";
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr File;
        public uint StateAction; public IntPtr StateData, Url; public uint ProviderFlags, Context; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
