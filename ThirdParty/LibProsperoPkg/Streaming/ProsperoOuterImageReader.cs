// Local streaming adaptation of LibProsperoPkg.
// Upstream copyright and licensing notices: ../LICENSE and upstream source headers.
using System;
using System.IO;
using System.Threading;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

// One decoded filesystem block is retained; package offsets remain 64-bit throughout.
internal sealed class ProsperoOuterImageReader : IMemoryReader
{
    private const int BlockSize = ProsperoOuterPfsImage.DefaultBlockSize;
    private readonly Stream source;
    private readonly long start, length, superblock, dataStart, dataEnd;
    private readonly byte[] tweak, data;
    private readonly CancellationToken cancellation;
    private readonly bool plaintext;
    private readonly byte[] block = new byte[BlockSize];
    private long cached = -1;

    internal ProsperoOuterImageReader(Stream source, long start, long length, long superblock,
        byte[] tweak, byte[] data, long dataStart = 0, long dataEnd = 0, CancellationToken cancellation = default, bool plaintext = false)
    {
        if (start < 0 || length <= 0 || length % BlockSize != 0 || start > source.Length || length > source.Length - start ||
            superblock < 0 || superblock >= length / BlockSize || dataStart < 0 || dataEnd < dataStart || dataEnd > length / BlockSize)
            throw new InvalidDataException("Invalid outer filesystem bounds.");
        this.source = source; this.start = start; this.length = length; this.superblock = superblock;
        this.tweak = tweak; this.data = data; this.dataStart = dataStart; this.dataEnd = dataEnd; this.cancellation = cancellation; this.plaintext = plaintext;
    }

    public void Read(long pos, byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (pos < 0 || pos > length || count < 0 || count > length - pos || offset < 0 || count > buffer.Length - offset)
            throw new EndOfStreamException("Outer filesystem read is outside its image.");
        lock (source)
        {
            while (count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                long index = pos / BlockSize;
                if (cached != index)
                {
                    cached = -1;
                    source.Position = checked(start + index * BlockSize);
                    source.ReadExactly(block);
                    if (!plaintext && index != superblock)
                    {
                        ulong sector = (ulong)index;
                        if (index < dataStart || index >= dataEnd) sector |= ProsperoOuterPfsSignature.SignedBlockTweakFlag;
                        using var xts = new XtsBlockTransform(data, tweak);
                        xts.CryptSector(block, sector, encrypt: false);
                    }
                    cached = index;
                }
                int within = (int)(pos % BlockSize);
                int take = Math.Min(count, BlockSize - within);
                Buffer.BlockCopy(block, within, buffer, offset, take);
                pos += take; offset += take; count -= take;
            }
        }
    }

    public void Dispose() { cached = -1; }
}
