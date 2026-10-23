using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PS5CatalogBrowser.Core;

/// <summary>Holds a source against writes/replacement and deletes by handle, never by a freshly resolved path.</summary>
public sealed class InstallationSource : IDisposable
{
    public FileStream Stream { get; }
    public string Identity { get; }
    private readonly bool deleteAccess;
    private readonly FileStream? ownership;
    public InstallationSource(string path, bool forDeletion = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Image installation requires Windows.");
        ImageFileSystem.EnsureNoLinks(path);
        deleteAccess = forDeletion;
        var handle = CreateFile(path, 0x80000000u | (forDeletion ? 0x10000u : 0), forDeletion ? 0u : 1u, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot exclusively own the source image for reading/deletion."); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Source is not a regular file.");
            Identity = $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}:{info.CreationHigh:X8}{info.CreationLow:X8}";
            var ownerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS5CatalogBrowser", "installation-ownership");
            ImageFileSystem.EnsureNoLinks(ownerRoot); Directory.CreateDirectory(ownerRoot);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Identity)));
            ownership = new FileStream(Path.Combine(ownerRoot, name + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            Stream = new FileStream(handle, FileAccess.Read, 1024 * 1024, false);
        }
        catch { ownership?.Dispose(); handle.Dispose(); throw; }
    }
    public static bool IsCandidate(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = ImageFileSystem.OpenRead(path);
            return HasSignature(path, stream);
        }
        catch { return false; }
    }
    public static bool CanOwn(string path)
    {
        try
        {
            using var lease = new InstallationSource(path);
            return HasSignature(path, lease.Stream);
        }
        catch { return false; }
    }
    public static bool HasSignature(string path, Stream stream)
    {
        if (Path.GetExtension(path).Equals(".exfat", StringComparison.OrdinalIgnoreCase)) return HasExFatBootSector(stream);
        if (!Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase) || stream.Length < 0x1000) return false;
        Span<byte> magic = stackalloc byte[4]; stream.Position = 0; stream.ReadExactly(magic); stream.Position = 0;
        return magic.SequenceEqual(new byte[] { 0x7f, 0x43, 0x4e, 0x54 }) || magic.SequenceEqual(new byte[] { 0x7f, 0x46, 0x49, 0x48 });
    }
    public static bool HasExFatBootSector(Stream stream)
    {
        var header = new byte[512]; stream.Position = 0;
        try { stream.ReadExactly(header); } catch (EndOfStreamException) { return false; }
        stream.Position = 0;
        if (!header.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) || header[510] != 0x55 || header[511] != 0xaa ||
            header[108] is not (>= 9 and <= 12) || header[109] > 25 - header[108] || header[110] is not (1 or 2)) return false;
        var sectors = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
        var fat = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        var fatSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        var heap = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88));
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(92));
        var root = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(96));
        return sectors >= 24 && sectors <= ((ulong)stream.Length >> header[108]) && fat >= 24 && fatSize > 0 && count > 0 &&
            heap >= fat + (ulong)fatSize * header[110] && heap + ((ulong)count << header[109]) <= sectors && root >= 2 && root <= (ulong)count + 1;
    }
    public void Delete()
    {
        if (!deleteAccess) throw new InvalidOperationException("Source lease does not own deletion access.");
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Installed game is preserved; source cleanup failed.");
    }
    public void Dispose() { Stream.Dispose(); ownership?.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref FileDisposition information, uint size);
}
