using System.IO;
using System.Runtime.InteropServices;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

internal static class LibraryRecycleBin
{
    public static void Recycle(LibraryDeletionTarget target)
    {
        var operation = new FileOperation
        {
            Function = 3, // FO_DELETE
            From = target.Path + "\0\0",
            // Recycle, suppress the duplicate ordinary confirmation, show progress,
            // warn before permanent deletion, and never include connected sibling folders.
            Flags = 0x0040 | 0x0010 | 0x0100 | 0x4000 | 0x2000
        };
        var result = SHFileOperation(ref operation);
        if (operation.Aborted) throw new OperationCanceledException("Windows file deletion was cancelled.");
        if (result != 0) throw new IOException($"Windows could not recycle {target.Path} (error 0x{result:X}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref FileOperation operation);
}
