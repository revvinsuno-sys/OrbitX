using System.Buffers.Binary;

namespace PS5CatalogBrowser.Core;

public static class GameExecutableInspection
{
    // Structural data-format checks matching the existing KytyPS5 ELF/SELF loader. No execution, decryption or patching.
    public static async Task ValidateAsync(string path, CancellationToken cancellation)
    {
        await using var file = ImageFileSystem.OpenRead(path);
        if (file.Length < 64) throw Invalid();
        var header = new byte[64]; await file.ReadExactlyAsync(header, cancellation);
        var self = header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x4f, 0x15, 0x3d, 0x1d }) ||
            header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x54, 0x14, 0xf5, 0xee });
        ulong elfPosition = 0;
        var selfDataSizes = new Dictionary<int, ulong>();
        if (self)
        {
            var first = header.AsSpan(4, 8).SequenceEqual(new byte[] { 0, 1, 1, 0x12, 1, 1, 0, 0 }) && U16(header, 26) == 0x22;
            var second = header.AsSpan(4, 8).SequenceEqual(new byte[] { 0x10, 1, 1, 0x12, 1, 1, 0, 0x10 }) && U16(header, 26) == 0x32;
            var count = U16(header, 24); var declared = U64(header, 16);
            if ((!first && !second) || count == 0 || declared == 0 || declared > (ulong)file.Length) throw Invalid();
            elfPosition = 32UL + (ulong)count * 32;
            if (!Fits(elfPosition, 64)) throw Invalid();
            var segment = new byte[32]; file.Position = 32;
            for (var i = 0; i < count; i++)
            {
                await file.ReadExactlyAsync(segment, cancellation);
                if (!Fits(U64(segment, 8), U64(segment, 16))) throw Invalid();
                var flags = U64(segment, 0);
                if ((flags & 0x800) != 0)
                {
                    if (U64(segment, 16) != U64(segment, 24) || !selfDataSizes.TryAdd((int)((flags >> 20) & 0xfff), U64(segment, 24)))
                        throw new InvalidDataException("SELF has compressed or ambiguous data segments unsupported by the inspected KytyPS5 loader. Original image will be preserved.");
                }
            }
            file.Position = (long)elfPosition; await file.ReadExactlyAsync(header, cancellation);
        }
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) ||
            header[4] != 2 || header[5] != 1 || header[6] != 1 || header[7] != 9 || header[8] is not (0 or 2) ||
            U16(header, 16) is not (0xfe10 or 0xfe18) || U16(header, 18) != 62 || U32(header, 20) != 1 ||
            U16(header, 52) != 64 || U16(header, 54) != 56 || U16(header, 58) is not (0 or 64)) throw Invalid();
        var programCount = U16(header, 56);
        var programOffset = U64(header, 32);
        if (programCount == 0 || programOffset < 64 || !Fits(checked(elfPosition + programOffset), (ulong)programCount * 56)) throw Invalid();
        if (selfDataSizes.Keys.Any(index => index >= programCount)) throw Invalid();
        if (!self && U16(header, 60) > 0 && (U16(header, 58) != 64 || !Fits(U64(header, 40), (ulong)U16(header, 60) * 64))) throw Invalid();
        file.Position = checked((long)(elfPosition + programOffset));
        var program = new byte[56]; var hasLoad = false;
        for (var i = 0; i < programCount; i++)
        {
            await file.ReadExactlyAsync(program, cancellation);
            var bytes = U64(program, 32);
            if (!self && bytes > 0 && !Fits(U64(program, 8), bytes)) throw Invalid();
            if (self && bytes > 0 && U32(program, 0) is 1 or 2 or 0x61000000 &&
                (!selfDataSizes.TryGetValue(i, out var mapped) || mapped != bytes)) throw Invalid();
            if (U32(program, 0) == 1 && bytes > 0 && U64(program, 40) >= bytes) hasLoad = true;
        }
        if (!hasLoad) throw Invalid();
        bool Fits(ulong position, ulong bytes) => position <= (ulong)file.Length && bytes <= (ulong)file.Length - position;
    }
    private static InvalidDataException Invalid() => new("eboot.bin is missing, truncated or does not meet the inspected KytyPS5 ELF/SELF structural requirements. Original image will be preserved.");
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
}
