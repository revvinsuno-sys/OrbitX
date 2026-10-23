// Local streaming adaptation of LibProsperoPkg.
// Upstream copyright and licensing notices: ../LICENSE and upstream source headers.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

internal sealed class ProsperoCompressedImageReader : IMemoryReader
{
    private readonly IMemoryReader source;
    private readonly ProsperoCompressedPfsFile container;
    private readonly CancellationToken cancellation;
    private byte[] decoded = Array.Empty<byte>();
    private int cached = -1;
    public long Length => container.UncompressedSize;

    internal ProsperoCompressedImageReader(IMemoryReader source, long sourceLength, CancellationToken cancellation = default)
    {
        this.source = source; this.cancellation = cancellation;
        cancellation.ThrowIfCancellationRequested();
        var prefix = new byte[0x58]; source.Read(0, prefix, 0, prefix.Length);
        if (!ProsperoCompressedPfsFile.IsPfsCompressed(prefix)) throw new InvalidDataException("Invalid PS5 PFSC header.");
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(6));
        if (sections == 0)
        {
            long first = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0x4a));
            sections = checked((int)((first - 0x48) / 16));
        }
        if (sections <= 0 || sections > 4096) throw new InvalidDataException("Invalid PS5 PFSC section count.");
        var directory = new byte[0x48 + sections * 16]; source.Read(0, directory, 0, directory.Length);
        long dataOffset = -1;
        for (int i = 0; i < sections; i++)
        {
            var entry = directory.AsSpan(0x48 + i * 16, 16);
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == 7)
                dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]) | ((long)BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) << 32);
        }
        if (dataOffset < directory.Length || dataOffset > 64 * 1024 * 1024 || dataOffset > sourceLength)
            throw new InvalidDataException("PS5 PFSC metadata is invalid or exceeds the 64 MiB metadata limit.");
        var metadata = new byte[(int)dataOffset]; source.Read(0, metadata, 0, metadata.Length);
        container = ProsperoCompressedPfsFile.ParseMetadata(metadata, sourceLength);
        if (container.TotalCompressedSize != sourceLength || !container.VerifyFileDigest())
            throw new InvalidDataException("PS5 PFSC length or metadata digest did not match its header.");
        long cursor = 0;
        foreach (var block in container.Blocks)
        {
            if (block.UncompressedOffset != cursor || block.UncompressedSize <= 0 || block.UncompressedSize > 0x40000 ||
                block.CompressedSize <= 0 || block.CompressedSize > 0x40000)
                throw new InvalidDataException("Invalid PS5 PFSC block geometry.");
            cursor = checked(cursor + block.UncompressedSize);
        }
        if (cursor != Length) throw new InvalidDataException("PS5 PFSC block index does not cover its logical image.");
    }

    public void Read(long pos, byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (pos < 0 || pos > Length || count < 0 || count > Length - pos || offset < 0 || count > buffer.Length - offset)
            throw new EndOfStreamException("PS5 PFSC read is outside its logical image.");
        while (count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int low = 0, high = container.Blocks.Count - 1;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (container.Blocks[middle].UncompressedOffset <= pos) low = middle; else high = middle - 1;
            }
            var block = container.Blocks[low];
            if (cached != low)
            {
                cached = -1;
                var stored = new byte[block.CompressedSize];
                source.Read(block.CompressedOffset, stored, 0, stored.Length);
                decoded = new byte[block.UncompressedSize];
                if (block.IsStored) stored.CopyTo(decoded, 0);
                else if (KrakenDecoder.DecodeBlock(stored, block.Flags, block.FirstChunkCompressedSize, decoded) != KrakenDecodeStatus.Success)
                    throw new InvalidDataException($"Cannot decode PS5 PFSC block {low}.");
                if (!block.Hash.IsEmpty && !ProsperoPfsDigest.VerifyBlockDigest(decoded, block.Hash.Span))
                    throw new InvalidDataException($"PS5 PFSC block {low} digest did not match.");
                cached = low;
            }
            int within = checked((int)(pos - block.UncompressedOffset));
            int take = Math.Min(count, decoded.Length - within);
            Buffer.BlockCopy(decoded, within, buffer, offset, take);
            pos += take; offset += take; count -= take;
        }
    }

    public void Dispose() { decoded = Array.Empty<byte>(); cached = -1; }
}
