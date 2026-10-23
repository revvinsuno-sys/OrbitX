using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PS5CatalogBrowser.App;

internal static class IgdbCredentialProtection
{
    internal static string Protect(string value) => Convert.ToBase64String(Transform(System.Text.Encoding.UTF8.GetBytes(value), protect: true));
    internal static string Unprotect(string value) => System.Text.Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), protect: false));

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var source = new DataBlob { Size = input.Length, Data = inputPointer };
            DataBlob output;
            var success = protect ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPointer); CryptographicOperations.ZeroMemory(input); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal int Size; internal IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
