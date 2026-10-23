using System.Buffers.Binary;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

internal static class StreamingPackageReaderTests
{
    const int Unit = 0x40000;
    public static Task RunAsync()
    {
        Outer(); Inner(); InnerRunBase(); InnerChunks(); FileTree(); PaddedInodes(); Compressed(2); Compressed(3); LargeCompressed();
        Console.WriteLine("PASS streaming readers: 64-bit offsets, bounded reads, reference bytes, integrity, cancellation and bounds");
        return Task.CompletedTask;
    }

    static void Outer()
    {
        var source = new VirtualReader(12L << 30);
        long position = (9L << 30) + 123;
        using var stream = new ProsperoReaderStream(source, 0, source.Length);
        using var reader = new ProsperoOuterImageReader(stream, 0, source.Length, 0, new byte[16], new byte[16], plaintext: true);
        Compare(reader, new VirtualReader(source.Length), position, 0x11000);
        Require(source.MaxRead <= 0x10000 && source.ReadBytes < 0x50000);
        Throws<EndOfStreamException>(() => reader.Read(source.Length - 2, new byte[3], 0, 3));
        using var cts = new CancellationTokenSource();
        using var cancelled = new ProsperoOuterImageReader(stream, 0, source.Length, 0, new byte[16], new byte[16], cancellation: cts.Token);
        cts.Cancel(); Throws<OperationCanceledException>(() => cancelled.Read(position, new byte[1], 0, 1));

        var plain = new byte[4 * 0x10000];
        for (int i = 0; i < plain.Length; i++) plain[i] = Pattern(i);
        var encrypted = (byte[])plain.Clone();
        byte[] data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), tweak = Enumerable.Range(32, 16).Select(i => (byte)i).ToArray();
        using (var xts = new XtsBlockTransform(data, tweak))
            for (int i = 1; i < 4; i++)
            {
                var block = plain.AsSpan(i * 0x10000, 0x10000).ToArray();
                xts.CryptSector(block, (ulong)(uint)i | (i == 2 ? 0UL : ProsperoOuterPfsSignature.SignedBlockTweakFlag), true);
                block.CopyTo(encrypted, i * 0x10000);
            }
        using var encryptedStream = new MemoryStream(encrypted, writable: false);
        using var decrypted = new ProsperoOuterImageReader(encryptedStream, 0, encrypted.Length, 0, tweak, data, 2, 3);
        var actual = new byte[plain.Length]; decrypted.Read(0, actual, 0, actual.Length);
        Require(actual.SequenceEqual(plain));
        Require(encryptedStream.ToArray().SequenceEqual(encrypted));
    }

    static byte[] Layout(long length)
    {
        int count = checked((int)((length + Unit - 1) / Unit));
        var counts = new NapsLayoutCounts(2, 2, 1, 0, count, 0, count + 1);
        int u2c = 16 + 2 * 6;
        int cbi = (u2c + counts.NumU2cEntries * 10 + 15) & ~15;
        var layout = new byte[(cbi + (count + 1) * 9 + 15) & ~15];
        ProsperoNapsLayout.EncodeHeader(counts).CopyTo(layout, 0);
        for (int j = 0; j < 5; j++) layout[22 + j] = (byte)(length >> (8 * j));
        layout[27] = 0x40;
        for (int u = 0; u <= count; u += 8)
        {
            int p = u2c + u / 8 * 10;
            layout[p] = (byte)u; layout[p + 1] = (byte)(u >> 8); layout[p + 2] = (byte)(u >> 16);
            for (int j = 1; j < 8; j++) layout[p + 2 + j] = (byte)j;
        }
        for (int i = 0; i <= count; i++)
        {
            ulong offset = (ulong)Math.Min(length, (long)i * Unit) & 0x3ffff;
            BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(cbi + i * 9), offset | (offset << 20));
        }
        return layout;
    }

    static void Inner()
    {
        long length = (5L << 30) + 0x10000;
        var source = new VirtualReader(length);
        using var reader = new ProsperoInnerImageReader(source, length, Layout(length), length);
        Compare(reader, new VirtualReader(length), (4L << 30) - 123, Unit + 999);
        Compare(reader, source, length - 93, 93);
        Require(source.MaxRead <= Unit && source.ReadBytes < 2 * 1024 * 1024);
        Throws<EndOfStreamException>(() => reader.Read(length, new byte[1], 0, 1));
        Throws<InvalidDataException>(() => new ProsperoInnerImageReader(source, Unit, Layout(length), length));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Throws<OperationCanceledException>(() => new ProsperoInnerImageReader(source, length, Layout(length), length, cts.Token));
        // Reference payload bytes, independent of the upstream builder's unsupported section geometry.
        var small = new byte[3 * 0x10000];
        for (int i = 0; i < small.Length; i++) small[i] = Pattern(i);
        var layout = Layout(small.Length);
        using var smallSource = new VirtualReader(small.Length, small);
        using var smallReader = new ProsperoInnerImageReader(smallSource, small.Length, layout, small.Length);
        var result = new byte[small.Length]; smallReader.Read(0, result, 0, result.Length);
        Require(result.SequenceEqual(small));
        Throws<InvalidDataException>(() => new ProsperoInnerImageReader(source, length, layout.AsSpan(0, layout.Length - 1), small.Length));
    }

    static byte[] Header(long length, ushort version)
    {
        int count = checked((int)((length + Unit - 1) / Unit));
        int boundary = 0x68, data = checked(boundary + (count + 1) * 16);
        var bytes = new byte[data];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ProsperoCompressedPfsFile.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), Unit);
        bytes[0x10] = 8; bytes[0x11] = 7; bytes[0x12] = 18;
        W64(bytes, 0x18, length); W64(bytes, 0x20, length + data);
        Section(bytes, 0x48, 3, boundary, (count + 1L) * 16);
        Section(bytes, 0x58, 7, data, length);
        for (int i = 0; i <= count; i++)
        {
            long offset = Math.Min(length, (long)i * Unit);
            W64(bytes, boundary + i * 16, offset); W64(bytes, boundary + i * 16 + 8, offset);
        }
        ProsperoPfsDigest.ComputeFileDigest(bytes.AsSpan(8, 24), [], bytes.AsSpan(boundary), []).CopyTo(bytes, 0x28);
        return bytes;
    }

    static void InnerRunBase()
    {
        const long stored = (9L << 30) + 0x19000;
        var layout = Layout(0x10000);
        ProsperoNapsLayout.EncodeHeader(new(2, 2, 1, 0, 1, 0, 3)).CopyTo(layout, 0);
        layout[28] = 1; // u2c points past the run marker to the first data record.
        // Packed run cursor: physical 256 KiB base at bit 50; an unrelated tweak at bit 20.
        ulong run = (1UL << 18) | (123UL << 20) | ((ulong)(stored >> 18) << 50);
        W64(layout, 48, unchecked((long)run));
        layout[56] = (byte)((ulong)(stored >> 18) >> 14);
        W64(layout, 57, stored & 0x3ffff);
        W64(layout, 66, ((stored + 0x10000) & 0x3ffff) | (0x10000L << 20));
        using var source = new VirtualReader(stored + 0x10000);
        using var reader = new ProsperoInnerImageReader(source, source.Length, layout);
        var actual = new byte[0x10000]; var expected = new byte[actual.Length];
        reader.Read(0, actual, 0, actual.Length); source.Read(stored, expected, 0, expected.Length);
        Require(actual.SequenceEqual(expected) && source.MaxRead <= Unit);
        layout[28] = 9;
        Throws<InvalidDataException>(() => new ProsperoInnerImageReader(source, source.Length, layout));
    }

    static void InnerChunks()
    {
        const int length = 0x30000, stored = 0x19000, first = 0x20000;
        var expected = Enumerable.Range(0, length).Select(i => Pattern(i)).ToArray();
        var payload = new byte[length + 3];
        Array.Copy(expected, payload, first);
        payload[first] = 1; // bare-entropy type 0, 24-bit length 0x010000
        Array.Copy(expected, first, payload, first + 3, length - first);
        var layout = Layout(length);
        ProsperoNapsLayout.EncodeHeader(new(2, 2, 1, 0, 1, 0, 3)).CopyTo(layout, 0);
        layout[28] = 1;
        W64(layout, 48, 1 << 18);
        W64(layout, 57, (long)stored | ((long)(first - 1) << 38) | (4L << 56));
        W64(layout, 66, ((stored + (long)payload.Length) & 0x3ffff) | ((long)length << 20));
        using var source = new VirtualReader(stored + payload.Length) { Overlay = payload, OverlayStart = stored };
        using var reader = new ProsperoInnerImageReader(source, source.Length, layout);
        var actual = new byte[length]; reader.Read(0, actual, 0, actual.Length);
        Require(actual.SequenceEqual(expected)); // full 17-bit chunk size, wrapped compressed cursor, mixed raw/entropy
    }

    static void FileTree()
    {
        long meta = 4L << 30;
        var bytes = new byte[4 * 0x10000];
        W64(bytes, 0, 2); W64(bytes, 8, 20130315);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20), 0x10000);
        W64(bytes, 0x30, 3);
        for (int i = 0; i < 3; i++)
        {
            int p = 0x10000 + i * 0xa8;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), (ushort)(i < 2 ? 0x4000 : 0x8000));
            W64(bytes, p + 8, i < 2 ? 0x10000 : (3L << 30) + 17);
            W64(bytes, p + 0x60, i < 2 ? meta + (i + 2) * 0x10000 : 0);
        }
        using (var root = new MemoryStream(bytes, 2 * 0x10000, 0x10000, true))
            new ProsperoPfsDirent { InodeNumber = 1, Type = ProsperoDirentType.Directory, Name = "uroot" }.WriteToStream(root);
        using (var root = new MemoryStream(bytes, 3 * 0x10000, 0x10000, true))
            new ProsperoPfsDirent { InodeNumber = 2, Type = ProsperoDirentType.File, Name = "large.bin" }.WriteToStream(root);
        using var source = new VirtualReader(meta + bytes.Length);
        source.Overlay = bytes; source.OverlayStart = meta;
        var files = ProsperoPs5InnerImageReader.ReadFileTree(source, source.Length, meta);
        Require(files.Count == 1 && files[0].Path == "large.bin" && files[0].Size == (3L << 30) + 17);
        Require(source.ReadBytes < 0x10000);
        W64(bytes, 0x10000 + 2 * 0xa8 + 8, source.Length + 1);
        Throws<InvalidDataException>(() => ProsperoPs5InnerImageReader.ReadFileTree(source, source.Length, meta));
    }

    static void Compressed(ushort version)
    {
        int length = Unit + 19; var header = Header(length, version);
        var full = new byte[header.Length + length]; header.CopyTo(full, 0);
        for (int i = 0; i < length; i++) full[header.Length + i] = Pattern(i);
        var legacy = ProsperoCompressedPfsFile.Parse(full).Decompress();
        using var source = new VirtualReader(full.Length, full);
        using var reader = new ProsperoCompressedImageReader(source, full.Length);
        var result = new byte[length]; reader.Read(0, result, 0, length);
        Require(result.SequenceEqual(legacy));
        full[0x28] ^= 1;
        Throws<InvalidDataException>(() => new ProsperoCompressedImageReader(source, full.Length));
        full[0x28] ^= 1;
        Throws<InvalidDataException>(() => new ProsperoCompressedImageReader(source, full.Length - 1));
    }

    static void PaddedInodes()
    {
        const int bs = 0x10000, count = 392, fileInode = count - 1;
        var bytes = new byte[6 * bs];
        W64(bytes, 0, 2); W64(bytes, 8, 20130315);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20), bs);
        W64(bytes, 0x30, count);
        Node(0, 0x4000, 3 * bs, bs); Node(1, 0x4000, 4 * bs, bs);
        Node(fileInode, 0x8000, 5 * bs, 7);
        // Inodes do not straddle PFS blocks; the 16-byte tail is not an inode prefix.
        bytes.AsSpan(bs + 390 * 0xa8, 16).Fill(0xff);
        using (var dir = new MemoryStream(bytes, 3 * bs, bs, true))
            new ProsperoPfsDirent { InodeNumber = 1, Type = ProsperoDirentType.Directory, Name = "uroot" }.WriteToStream(dir);
        using (var dir = new MemoryStream(bytes, 4 * bs, bs, true))
            new ProsperoPfsDirent { InodeNumber = fileInode, Type = ProsperoDirentType.File, Name = "padded.bin" }.WriteToStream(dir);
        using var source = new VirtualReader(bytes.Length, bytes);
        var files = ProsperoPs5InnerImageReader.ReadFileTree(source, bytes.Length, 0);
        Require(files.Count == 1 && files[0].Path == "padded.bin" && files[0].Size == 7 && files[0].LogicalOffset == 5 * bs);
        Throws<InvalidDataException>(() => ProsperoPs5InnerImageReader.ReadFileTree(source, 2 * bs + 0xa8, 0));
        void Node(int i, ushort mode, long offset, long size)
        {
            int p = bs + i / 390 * bs + i % 390 * 0xa8;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), mode);
            W64(bytes, p + 8, size); W64(bytes, p + 0x60, offset);
        }
    }

    static void LargeCompressed()
    {
        long length = (5L << 30) + 37; var header = Header(length, 3);
        using var source = new VirtualReader(length + header.Length, header, header.Length);
        using var reader = new ProsperoCompressedImageReader(source, source.Length);
        var result = new byte[Unit + 99]; long pos = (4L << 30) - 77;
        reader.Read(pos, result, 0, result.Length);
        Require(result.Select((value, i) => value == Pattern(pos + i)).All(x => x));
        Require(source.MaxRead <= header.Length && source.ReadBytes < 3 * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        using var cancelled = new ProsperoCompressedImageReader(source, source.Length, cts.Token);
        cts.Cancel(); Throws<OperationCanceledException>(() => cancelled.Read(0, new byte[1], 0, 1));
        Throws<EndOfStreamException>(() => reader.Read(length - 1, new byte[2], 0, 2));
    }

    static void Section(byte[] b, int p, ushort id, long offset, long length)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), id);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 2), (uint)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 6), (ushort)(offset >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 10), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 14), (ushort)(length >> 32));
    }
    static void W64(byte[] b, int p, long value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), (ulong)value);
    static byte Pattern(long p) => (byte)((p * 31 + (p >> 16) + (p >> 32)) & 255);
    static void Compare(IMemoryReader actual, IMemoryReader expected, long position, int count)
    {
        var a = new byte[count]; var b = new byte[count];
        actual.Read(position, a, 0, count); expected.Read(position, b, 0, count); Require(a.SequenceEqual(b));
    }
    static void Require(bool condition) { if (!condition) throw new Exception("Streaming reader assertion failed."); }
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    sealed class VirtualReader(long length, byte[]? prefix = null, long patternBase = 0) : IMemoryReader
    {
        public long Length => length;
        public int MaxRead; public long ReadBytes;
        public byte[]? Overlay; public long OverlayStart;
        public void Read(long pos, byte[] buffer, int offset, int count)
        {
            if (pos < 0 || count < 0 || pos > length || count > length - pos) throw new EndOfStreamException();
            MaxRead = Math.Max(MaxRead, count); ReadBytes += count;
            for (int i = 0; i < count; i++) buffer[offset + i] = prefix is not null && pos + i < prefix.Length ? prefix[(int)(pos + i)] : Pattern(pos + i - patternBase);
            if (Overlay is not null)
            {
                long first = Math.Max(pos, OverlayStart), last = Math.Min(pos + count, OverlayStart + Overlay.Length);
                if (last > first) Array.Copy(Overlay, first - OverlayStart, buffer, offset + first - pos, last - first);
            }
        }
        public void Dispose() { }
    }
}
