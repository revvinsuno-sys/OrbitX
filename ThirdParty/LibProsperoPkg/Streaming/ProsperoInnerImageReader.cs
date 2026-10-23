// Local streaming adaptation of LibProsperoPkg.
// Upstream copyright and licensing notices: ../LICENSE and upstream source headers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibProsperoPkg.PKG;
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

// The layout is an index, not a reconstructed image. Only one 256 KiB decoded block is cached.
internal sealed class ProsperoInnerImageReader : IMemoryReader
{
    private const int Unit = 0x40000;
    private readonly record struct Block(long Logical, long Stored, int Length, int Compressed, int Even, bool Kraken, int Flags);
    private readonly IMemoryReader source;
    private readonly Block[] blocks;
    private readonly CancellationToken cancellation;
    private byte[] decoded = Array.Empty<byte>();
    private int cached = -1;
    public long Length { get; }
    public long MetadataStart { get; }

    internal ProsperoInnerImageReader(IMemoryReader source, long storedLength, ReadOnlySpan<byte> layout,
        long mountSize = 0, CancellationToken cancellation = default)
    {
        this.source = source; this.cancellation = cancellation;
        var counts = ProsperoNapsLayout.DecodeHeader(layout);
        var map = ProsperoNapsLayout.SectionMap(counts, counts.NumFiles);
        long blockStart = checked((map.CblockInfo.Offset + 15) & ~15L);
        long contentEnd = checked(blockStart + map.CblockInfo.Size);
        if (contentEnd > layout.Length || ((contentEnd + 15) & ~15L) != layout.Length ||
            counts.NumShufflePatterns != 0 || counts.CompressionType != 2)
            throw new InvalidDataException("Unsupported or truncated NAPS layout geometry.");
        // Finalized layouts count the mount-end file marker and align the block table separately.
        // The upstream serializer's NumFiles+1 and contiguous section model do not describe these bytes.
        var offsets = new long[counts.NumFiles];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = checked((long)ProsperoNapsLayout.DecodeFileOffsetEntry(layout.Slice(checked((int)map.UncompressedOffsetStartByFileIdx.Offset + i * 6), 6)).UncompressedOffsetStart);
        if (mountSize == 0) mountSize = offsets[^1];
        if (storedLength <= 0 || mountSize <= 0 || (mountSize & 0xffff) != 0 || offsets[^1] != mountSize ||
            counts.NumUBlocks != (mountSize + Unit - 1) / Unit)
            throw new InvalidDataException("Invalid inner filesystem length.");
        Length = mountSize;
        MetadataStart = offsets.Where(offset => offset > 0 && offset < Length).DefaultIfEmpty().Max();
        var entries = new NapsCblockInfoEntry[counts.NumCblockInfo];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = ProsperoNapsLayout.DecodeCblockInfoEntry(layout.Slice(checked((int)blockStart + i * 9), 9));
        var anchors = new Dictionary<int, long>();
        for (int u = 0; u < counts.NumUBlocks; u += 8)
        {
            cancellation.ThrowIfCancellationRequested();
            var group = ProsperoNapsLayout.DecodeU2cEntry(layout.Slice(checked((int)map.CblockInfoOffsetByUblockIdxCompressed.Offset + u / 8 * 10), 10));
            var indices = group.StartCblockInfoIndex;
            for (int j = 0; j < 8 && u + j < counts.NumUBlocks; j++)
            {
                int ix = checked((int)indices[j]);
                if (ix >= entries.Length || entries[ix].IsRunBase)
                    throw new InvalidDataException("Invalid NAPS uncompressed-block reference.");
                anchors.TryAdd(ix, (long)(u + j) * Unit + UncompressedOffset(entries[ix]));
            }
        }
        var index = new List<Block>();
        long stored = 0, logical = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.IsRunBase)
            {
                long fraction = i + 1 < entries.Length && !entries[i + 1].IsRunBase ? entries[i + 1].CoffsetStartMod256K : 0;
                // The upstream field model includes an extra low bit in the 256 KiB base.
                // Keep every fractional offset bit; the AES-XTS tweak is not a byte cursor.
                stored = checked(((long)(entry.CoffsetStart256K >> 1) << 18) + fraction);
                continue;
            }
            if (logical == Length || i + 1 >= entries.Length) break;
            if ((stored & 0x3ffff) != entry.CoffsetStartMod256K) throw new InvalidDataException($"Mismatched stored offset at block {i}.");
            if (anchors.TryGetValue(i, out var anchor))
            {
                if (anchor < logical) throw new InvalidDataException("Overlapping NAPS logical blocks.");
                logical = anchor;
            }
            ulong word = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(entry.Raw);
            int uoffset = UncompressedOffset(entry);
            if ((logical & 0x3ffff) != uoffset) throw new InvalidDataException($"Mismatched logical offset at block {i}: {logical}, {uoffset}.");
            int nextIndex = i + 1;
            while (nextIndex < entries.Length && entries[nextIndex].IsRunBase) nextIndex++;
            if (nextIndex >= entries.Length) throw new InvalidDataException("Missing inner block sentinel.");
            ulong nextWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(entries[nextIndex].Raw);
            int length = ((int)((nextWord >> 20) & 0x3ffff) - uoffset + Unit) % Unit;
            if (length == 0) length = Unit;
            var next = entries[i + 1];
            long nextRelative = next.IsRunBase ? next.CoffsetEndMod256K : next.CoffsetStartMod256K;
            int compressed = checked((int)((nextRelative - entry.CoffsetStartMod256K + Unit) % Unit));
            if (compressed == 0) compressed = Unit;
            bool kraken = compressed != length;
            int storedBytes = compressed;
            if (stored < 0 || stored > storedLength || storedBytes <= 0 || storedBytes > Unit || storedBytes > storedLength - stored || length > Length - logical)
                throw new InvalidDataException($"Invalid inner block {i} at logical offset {logical}: stored={stored}, length={length}, compressed={compressed}, predictor={entry.KdePredictor}, currentMod={entry.CoffsetStartMod256K}, nextMod={nextRelative}, inputLength={storedLength}.");
            int flags = (entry.Raw[7] & 3) | ((entry.Raw[7] >> 3 & 3) << 4);
            index.Add(new(logical, stored, length, compressed, checked((int)((word >> 38) & 0x1ffff) + 1), kraken, flags));
            logical = checked(logical + length); stored = checked(stored + storedBytes);
        }
        blocks = index.ToArray();
        if (logical != Length) throw new InvalidDataException($"The package layout does not cover the complete inner filesystem: indexed={logical}, expected={Length}, last={index.LastOrDefault()}.");
    }

    private static int UncompressedOffset(NapsCblockInfoEntry entry)
        => (int)((System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(entry.Raw) >> 20) & 0x3ffff);

    public long FindSuperblock()
    {
        var signature = new byte[16];
        for (long pos = Length - 0x10000; pos >= MetadataStart; pos -= 0x10000)
        {
            Read(pos, signature, 0, signature.Length);
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(signature) == 2 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(signature.AsSpan(8)) == 20130315) return pos;
        }
        throw new InvalidDataException("No inner PFS superblock was found in the indexed metadata region.");
    }

    public void Read(long pos, byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (pos < 0 || pos > Length || count < 0 || count > Length - pos || offset < 0 || count > buffer.Length - offset)
            throw new EndOfStreamException("Inner filesystem read is outside its image.");
        while (count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int low = 0, high = blocks.Length - 1;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (blocks[middle].Logical <= pos) low = middle; else high = middle - 1;
            }
            var block = blocks[low];
            if (pos < block.Logical || pos >= block.Logical + block.Length)
                throw new InvalidDataException("Inner filesystem layout contains an unindexed gap.");
            if (cached != low)
            {
                cached = -1;
                decoded = new byte[block.Length];
                if (!block.Kraken) source.Read(block.Stored, decoded, 0, decoded.Length);
                else
                {
                    var compressed = new byte[block.Compressed];
                    source.Read(block.Stored, compressed, 0, compressed.Length);
                    int firstChunk = block.Length > 0x20000 ? block.Even : 0;
                    KrakenDecodeStatus status;
                    try { status = KrakenDecoder.DecodeBlock(compressed, block.Flags, firstChunk, decoded); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { throw new InvalidDataException($"Invalid Kraken block at logical offset {block.Logical}.", ex); }
                    if (status != KrakenDecodeStatus.Success)
                        throw new InvalidDataException($"Cannot decode Kraken block at logical offset {block.Logical}: {status}.");
                }
                cached = low;
            }
            int within = checked((int)(pos - block.Logical));
            int take = Math.Min(count, decoded.Length - within);
            if (take <= 0) throw new InvalidDataException("Inner filesystem layout contains a gap.");
            Buffer.BlockCopy(decoded, within, buffer, offset, take);
            pos += take; offset += take; count -= take;
        }
    }

    public void Dispose() { decoded = Array.Empty<byte>(); cached = -1; }
}

// A bounded Stream view lets the existing binary metadata parsers retain 64-bit positions.
internal sealed class ProsperoReaderStream(IMemoryReader reader, long start, long length) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set { if (value < 0 || value > length) throw new EndOfStreamException(); position = value; } }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || count > buffer.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
        int take = (int)Math.Min(count, length - position);
        reader.Read(checked(start + position), buffer, offset, take); position += take;
        return take;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        return position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
