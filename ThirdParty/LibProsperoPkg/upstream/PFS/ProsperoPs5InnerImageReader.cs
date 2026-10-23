// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// ---------------------------------------------------------------------------------------------------
// PS5 nwonly INNER pfs_image.dat READER (the inverse of ProsperoPs5InnerImageAssembler). A data-first
// inner image has NO on-disk block table: it is a raw concatenation of per-file payloads (raw or
// header-stripped Kraken chunks) followed by the Kraken-compressed metadata region. The per-block
// geometry (on-disk offset, compressed/uncompressed sizes, raw-vs-Kraken flag, even/odd split, byte
// shuffle) lives ONLY in the sibling naps_pkg_layout.dat's CblockInfo section.
//
// This reconstructs the uncompressed PFS mount from (inner image bytes + naps bytes) so the standard
// ProsperoPfsReader can walk it. The naps CblockInfo section supplies the per-block decode geometry for
// all file data and the metadata region.
// ---------------------------------------------------------------------------------------------------
#nullable enable
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.PKG;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>A regular file discovered in a reconstructed inner mount.</summary>
public sealed class ProsperoPs5InnerFileEntry
{
    /// <summary>User-root-relative path (e.g. <c>sce_sys/keystone</c> or <c>application.ps.bundle</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Byte offset of the file's (already decoded) data within the reconstructed mount.</summary>
    public required ulong LogicalOffset { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long Size { get; init; }
}

/// <summary>The reconstructed inner PFS mount plus the offset of its superblock.</summary>
public sealed class ProsperoPs5InnerMountResult
{
    /// <summary>The uncompressed inner PFS mount bytes (walk with <see cref="ProsperoPfsReader"/>).</summary>
    public required byte[] Mount { get; init; }

    /// <summary>
    /// Byte offset of the PFS superblock within <see cref="Mount"/>. A nwonly inner image is data-first,
    /// so the superblock is at the metadata-region base (not offset 0).
    /// </summary>
    public required long SuperblockOffset { get; init; }
}

/// <summary>
/// Reconstructs the uncompressed inner PFS mount of a PS5 nwonly package from its data-first
/// <c>pfs_image.dat</c> and the sibling <c>naps_pkg_layout.dat</c>. The result is a plaintext PFS
/// image whose superblock is at the metadata-region base that <see cref="ProsperoPfsReader"/> can walk.
/// </summary>
public static class ProsperoPs5InnerImageReader
{
    private const int Chunk128K = 0x20000;   // even/odd sub-chunk split
    private const long Ublock256K = 0x40000; // one CblockInfo STD covers up to one 256K ublock

    /// <summary>
    /// Reconstructs the uncompressed PFS mount from a data-first inner image and its naps layout. Files
    /// and the metadata region are decoded to their logical offsets; unreferenced inter-file padding is
    /// left zero-filled.
    /// </summary>
    /// <param name="innerImage">The on-disk data-first <c>pfs_image.dat</c> bytes.</param>
    /// <param name="naps">The <c>naps_pkg_layout.dat</c> bytes (its CblockInfo drives the decode).</param>
    /// <param name="mountSize">
    /// The uncompressed mount size (Ndblock·64K). Pass 0 to derive it from the naps: the mount end is the
    /// largest 64K-aligned <c>fidx</c> boundary (trailing typed sentinels are not block-aligned).
    /// </param>
    public static ProsperoPs5InnerMountResult ReconstructMount(
        ReadOnlySpan<byte> innerImage, ReadOnlySpan<byte> naps, long mountSize = 0)
    {
        NapsLayoutDocument doc = ProsperoNapsLayout.Parse(naps);
        IReadOnlyList<NapsCblockInfoEntry> cb = doc.CblockInfos;

        long[] rawOffsets = doc.FileOffsets.Select(f => (long)f.UncompressedOffsetStart).ToArray();
        if (mountSize <= 0)
        {
            // Derive the mount size: the mount end is a 64K-aligned fidx boundary; typed/garbage sentinels
            // are not block-aligned, so the largest block-aligned boundary is Ndblock*64K.
            mountSize = rawOffsets.Where(v => v > 0 && (v & 0xFFFF) == 0).DefaultIfEmpty(0).Max();
        }
        if (mountSize <= 0 || mountSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mountSize), mountSize, "Implausible inner mount size.");

        // fidx = uncompressed file-offset boundaries. Only values inside the mount are real block
        // boundaries; trailing sentinels (huge/typed) are ignored by the min-above-cursor lookup.
        long[] boundaries = rawOffsets
            .Where(v => v > 0 && v <= mountSize)
            .Distinct().OrderBy(v => v).ToArray();

        // The metadata region base (= superblock offset) is the last file-offset boundary below the mount
        // end; the data files occupy everything below it and the metadata PFS is stored above it.
        long metaBase = boundaries.LastOrDefault(v => v < mountSize);

        var mount = new byte[mountSize];
        byte[] innerArr = innerImage.ToArray();

        long onDisk = 0, uncompOff = 0;
        for (int i = 0; i < cb.Count; i++)
        {
            NapsCblockInfoEntry e = cb[i];
            if (e.IsRunBase)
            {
                // On-disk cursor re-anchors at each run: 32K-granular base + the fractional byte offset
                // carried in the following STD's low-15 coffMod bits.
                long frac = (i + 1 < cb.Count && !cb[i + 1].IsRunBase)
                    ? (cb[i + 1].CoffsetStartMod256K & 0x7fff) : 0;
                onDisk = ((long)e.TweakIdxStart << 15) + frac;
                continue;
            }

            long fileEnd = NextBoundary(boundaries, uncompOff, mountSize);
            long uncompLen = Math.Min(Ublock256K, fileEnd - uncompOff);
            if (uncompLen <= 0 || i + 1 >= cb.Count)
                break;

            int evenComp = (int)(e.ClenEvenMinus1 / 2 + 1);
            NapsCblockInfoEntry next = cb[i + 1];
            long thisRel = e.CoffsetStartMod256K;
            long nextRel = next.IsRunBase ? next.CoffsetEndMod256K : next.CoffsetStartMod256K;
            int totalComp = (int)(nextRel - thisRel);
            bool kraken = e.KdePredictor == 2;

            DecodeBlockInto(innerArr, onDisk, totalComp, evenComp, (int)uncompLen, kraken,
                            mount, (int)uncompOff);

            uncompOff += uncompLen;
            onDisk += kraken ? totalComp : uncompLen;
        }

        // Locate the metadata superblock (version 2 + magic 20130315) at a 64K boundary. The metadata PFS
        // sits at the top of the mount, so scan downward and take the highest match; fall back to the
        // fidx-derived base. This is robust to per-package fidx layout differences.
        long superblockOffset = metaBase;
        for (long p = (mountSize - 0x10000) & ~0xFFFFL; p >= 0; p -= 0x10000)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(mount.AsSpan((int)p)) == 2 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(mount.AsSpan((int)p + 8)) == 20130315)
            {
                superblockOffset = p;
                break;
            }
        }

        return new ProsperoPs5InnerMountResult { Mount = mount, SuperblockOffset = superblockOffset };
    }

    /// <summary>
    /// Parses the reconstructed inner mount's PS5 metadata (superblock + inode table + dirents) and returns
    /// every regular file under <c>uroot</c> with its user-root-relative path, logical offset and size. The
    /// file bytes are <c>mount[LogicalOffset .. LogicalOffset+Size]</c> (already decoded by
    /// <see cref="ReconstructMount"/>). The PS5 inner inode layout (LogicalOffset@0x60, parent@0x6c)
    /// differs from the layout <see cref="ProsperoPfsReader"/> reads, so this does not use it.
    /// </summary>
    public static IReadOnlyList<ProsperoPs5InnerFileEntry> ReadFileTree(byte[] mount, long metaBase)
    {
        using var input = new LibProsperoPkg.Util.StreamReader(new MemoryStream(mount, writable: false), takeOwnership: true);
        return ReadFileTree(input, mount.LongLength, metaBase);
    }

    /// <summary>Reads metadata through a bounded reader without materializing the logical image.</summary>
    public static IReadOnlyList<ProsperoPs5InnerFileEntry> ReadFileTree(IMemoryReader mount, long mountLength, long metaBase)
    {
        if (metaBase < 0 || mountLength < 0x70 || metaBase > mountLength - 0x70)
            throw new InvalidDataException("Inner superblock is outside its image.");
        var header = new byte[0x70]; mount.Read(metaBase, header, 0, header.Length);
        ReadOnlySpan<byte> sb = header;
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb) != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb[8..]) != 20130315)
            throw new InvalidDataException("Inner mount metadata does not start with a PS5 PFS superblock.");
        uint blockSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[0x20..]);
        long inodeCount = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb[0x30..]);
        if (blockSize < 0xa8 || blockSize > 0x100000 || inodeCount <= 0 || inodeCount > 1_000_000)
            throw new InvalidDataException("Implausible inner metadata block size / inode count.");
        long inodeTable = checked(metaBase + blockSize);
        long inodesPerBlock = blockSize / 0xa8;
        long lastInodeEnd = checked(inodeTable + ((inodeCount - 1) / inodesPerBlock) * blockSize + ((inodeCount - 1) % inodesPerBlock + 1) * 0xa8);
        if (lastInodeEnd > mountLength)
            throw new InvalidDataException("Inner inode table is outside its image.");
        var nodes = new InnerInode[(int)inodeCount];
        var bytes = new byte[0xa8];
        for (int i = 0; i < nodes.Length; i++)
        {
            long inodePosition = inodeTable + (i / inodesPerBlock) * blockSize + (i % inodesPerBlock) * bytes.Length;
            mount.Read(inodePosition, bytes, 0, bytes.Length);
            ReadOnlySpan<byte> entry = bytes;
            var node = new InnerInode
            {
                Mode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(entry),
                Size = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(entry[8..]),
                LogicalOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(entry[0x60..]),
            };
            if (node.Size < 0 || node.LogicalOffset > (ulong)mountLength || (ulong)node.Size > (ulong)mountLength - node.LogicalOffset)
                throw new InvalidDataException("Inner inode data is outside its image.");
            nodes[i] = node;
        }
        var files = new List<ProsperoPs5InnerFileEntry>();
        WalkDir(mount, mountLength, nodes, 0, "", false, files, new HashSet<uint>());
        return files;
    }

    private sealed class InnerInode
    {
        public ushort Mode;
        public long Size;
        public ulong LogicalOffset;
        public bool IsDirectory => (Mode & 0x4000) != 0;
    }

    private static void WalkDir(IMemoryReader mount, long mountLength, InnerInode[] nodes, uint dirInode,
        string path, bool underUroot, List<ProsperoPs5InnerFileEntry> files, HashSet<uint> seen)
    {
        if (dirInode >= nodes.Length) throw new InvalidDataException("Invalid inner directory inode.");
        if (!seen.Add(dirInode)) return;
        var dir = nodes[dirInode];
        if (!dir.IsDirectory || dir.Size <= 0) return;
        if (path.Count(c => c == '/') > 128) throw new InvalidDataException("Inner directory nesting is too deep.");
        using var input = new ProsperoReaderStream(mount, checked((long)dir.LogicalOffset), dir.Size);
        var recordHeader = new byte[16];
        while (input.Position + 0x10 <= input.Length)
        {
            long entryPosition = input.Position;
            input.ReadExactly(recordHeader);
            int nameLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(8));
            int recordLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(12));
            if (recordLength == 0) break;
            if (recordLength < 0x10 || recordLength > input.Length - entryPosition || nameLength < 0 || nameLength > recordLength - 16)
                throw new InvalidDataException("Invalid inner directory record length.");
            input.Position = entryPosition;
            var entry = ProsperoPfsDirent.ReadFromStream(input);
            input.Position = entryPosition + entry.EntSize;
            string name = entry.Name;
            if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;
            if (entry.InodeNumber >= nodes.Length) throw new InvalidDataException("Invalid inner directory entry inode.");
            bool childUnderUroot = underUroot || name == "uroot";
            string childPath = path.Length == 0 ? name == "uroot" ? "" : name : path + "/" + name;
            if (entry.Type == ProsperoDirentType.Directory)
                WalkDir(mount, mountLength, nodes, entry.InodeNumber, childPath, childUnderUroot, files, seen);
            else if (entry.Type == ProsperoDirentType.File && underUroot && childPath.Length > 0)
                files.Add(new ProsperoPs5InnerFileEntry { Path = childPath, LogicalOffset = nodes[entry.InodeNumber].LogicalOffset, Size = nodes[entry.InodeNumber].Size });
        }
    }

    private static long NextBoundary(long[] boundaries, long cur, long mountSize)
    {
        foreach (long b in boundaries)
            if (b > cur)
                return b;
        return mountSize;
    }

    // Decode a single block (files + metadata) into the mount at mountOff. Raw blocks copy uncompLen
    // bytes; Kraken blocks decode even (seeded) + odd (seedless, back-referencing even in the same
    // 256K buffer). Unreferenced padding blocks may copy garbage past their real footprint, which is
    // harmless because no inode points into the inter-file padding region.
    // NOTE: the naps records a per-block shuffle predictor, but the data-first payloads (data files and
    // the metadata region) are stored UN-shuffled on disk — decoding them yields the final plaintext
    // directly (applying the de-interleave would corrupt them). A package with genuinely shuffled blocks
    // would need a per-64K-sub-block de-interleave reintroduced here.
    private static void DecodeBlockInto(byte[] inner, long onDisk, int totalComp, int evenComp,
        int uncompLen, bool kraken, byte[] mount, int mountOff)
    {
        int room = mount.Length - mountOff;
        if (room <= 0)
            return;
        int copyLen = Math.Min(uncompLen, room);

        if (!kraken)
        {
            // Raw store: copy the uncompressed bytes straight through (bounded by the image length).
            if (onDisk >= 0 && onDisk < inner.Length)
            {
                int avail = (int)Math.Min(copyLen, inner.Length - onDisk);
                Array.Copy(inner, onDisk, mount, mountOff, avail);
            }
            return;
        }

        if (onDisk < 0 || onDisk + totalComp > inner.Length || totalComp <= 0)
            return;

        var src = inner.AsSpan((int)onDisk, totalComp);
        int firstChunkComp = uncompLen > Chunk128K ? evenComp : 0;
        var decoded = new byte[uncompLen];

        // The per-chunk newLZ/literal-mode flag bits are not stored in the packed 9-byte CblockInfo, so
        // select them by trying the observed combinations and taking the first that decodes cleanly.
        // Order favours the common case (both sub-chunks newLZ + raw-literal, then sub/delta variants).
        foreach (int flags in FlagCandidates(uncompLen > Chunk128K))
        {
            KrakenDecodeStatus st;
            try { st = KrakenDecoder.DecodeBlock(src, flags, firstChunkComp, decoded); }
            catch { continue; }
            if (st != KrakenDecodeStatus.Success)
                continue;
            Array.Copy(decoded, 0, mount, mountOff, copyLen);
            return;
        }
    }

    private static int[] FlagCandidates(bool multiChunk) => multiChunk
        ? [0x22, 0x02, 0x12, 0x32, 0x23, 0x03, 0x13, 0x33, 0x00, 0x20]
        : [0x02, 0x00, 0x03, 0x01];
}
