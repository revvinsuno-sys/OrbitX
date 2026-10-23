using System.Buffers.Binary;
using System.IO;
using System.Text;

// A small, self-contained exFAT filesystem for driver integration tests. Contains only generated test data.
internal static class SyntheticExFat
{
    private const int SectorSize = 512, ClusterSize = 4096, FatOffset = 24, FatLength = 128, HeapOffset = FatOffset + FatLength;
    private const int Sectors = 131072, ClusterCount = (Sectors - HeapOffset) / 8;
    public static void Create(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        file.SetLength((long)Sectors * SectorSize);
        var boot = new byte[12 * SectorSize]; boot[0] = 0xeb; boot[1] = 0x76; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot, 3);
        U64(boot, 72, Sectors); U32(boot, 80, FatOffset); U32(boot, 84, FatLength); U32(boot, 88, HeapOffset);
        U32(boot, 92, ClusterCount); U32(boot, 96, 2); U32(boot, 100, 0x543210ab); U16(boot, 104, 0x0100);
        boot[108] = 9; boot[109] = 3; boot[110] = 1; boot[111] = 0x80; boot[112] = 1;
        boot[510] = 0x55; boot[511] = 0xaa;
        for (var sector = 1; sector <= 8; sector++) U32(boot, (sector + 1) * SectorSize - 4, 0xaa550000);
        uint bootChecksum = 0;
        for (var i = 0; i < 11 * SectorSize; i++) if (i is not (106 or 107 or 112)) bootChecksum = Rotate(bootChecksum) + boot[i];
        for (var i = 11 * SectorSize; i < boot.Length; i += 4) U32(boot, i, bootChecksum);
        file.Write(boot); file.Write(boot);

        var upcase = new byte[65536 * 2]; uint upcaseChecksum = 0;
        for (var i = 0; i < 65536; i++) U16(upcase, i * 2, char.ToUpperInvariant((char)i));
        foreach (var value in upcase) upcaseChecksum = Rotate(upcaseChecksum) + value;
        var fat = new byte[FatLength * SectorSize]; U32(fat, 0, 0xfffffff8); U32(fat, 4, 0xffffffff);
        for (var cluster = 2; cluster <= 40; cluster++) U32(fat, cluster * 4, cluster is >= 4 and < 35 ? cluster + 1 : 0xffffffff);
        file.Position = FatOffset * SectorSize; file.Write(fat);

        var bitmap = new byte[(ClusterCount + 7) / 8];
        for (var cluster = 2; cluster <= 40; cluster++) bitmap[(cluster - 2) / 8] |= (byte)(1 << ((cluster - 2) % 8));
        WriteCluster(3, bitmap); WriteCluster(4, upcase);
        var root = new byte[ClusterSize];
        root[0] = 0x81; U32(root, 20, 3); U64(root, 24, bitmap.Length);
        root[32] = 0x82; U32(root, 36, upcaseChecksum); U32(root, 52, 4); U64(root, 56, upcase.Length);
        FileEntries("nested", 36, ClusterSize, true).CopyTo(root, 64); WriteCluster(2, root);
        var nested = new byte[ClusterSize]; FileEntries("game", 37, ClusterSize, true).CopyTo(nested, 0); WriteCluster(36, nested);
        var executable = TestExecutable();
        var metadata = Encoding.UTF8.GetBytes("{\"titleId\":\"PPSA00001\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"Synthetic exFAT Fixture\"}}}");
        var game = new byte[ClusterSize];
        FileEntries("sce_sys", 38, ClusterSize, true).CopyTo(game, 0);
        FileEntries("eboot.bin", 39, executable.Length, false).CopyTo(game, 96); WriteCluster(37, game);
        var system = new byte[ClusterSize]; FileEntries("param.json", 40, metadata.Length, false).CopyTo(system, 0); WriteCluster(38, system);
        WriteCluster(39, executable); WriteCluster(40, metadata); file.Flush(true);
        void WriteCluster(int number, byte[] data) { file.Position = (long)HeapOffset * SectorSize + (long)(number - 2) * ClusterSize; file.Write(data); }
    }
    public static byte[] TestExecutable()
    {
        var elf = new byte[152]; elf[0] = 0x7f; elf[1] = 0x45; elf[2] = 0x4c; elf[3] = 0x46; elf[4] = 2; elf[5] = 1; elf[6] = 1; elf[7] = 9;
        U16(elf, 16, 0xfe10); U16(elf, 18, 62); U32(elf, 20, 1); U64(elf, 24, 0x400078); U64(elf, 32, 64);
        U16(elf, 52, 64); U16(elf, 54, 56); U16(elf, 56, 1);
        U32(elf, 64, 1); U32(elf, 68, 5); U64(elf, 72, 120); U64(elf, 80, 0x400078); U64(elf, 88, 0x400078);
        U64(elf, 96, 32); U64(elf, 104, 32); U64(elf, 112, 4096); elf[120] = 0xc3;
        return elf;
    }
    private static byte[] FileEntries(string name, int cluster, int length, bool directory)
    {
        var data = new byte[96]; data[0] = 0x85; data[1] = 2; U16(data, 4, directory ? 0x10 : 0x20);
        U32(data, 8, 0x00210000); U32(data, 12, 0x00210000); U32(data, 16, 0x00210000);
        data[22] = data[23] = data[24] = 0x80;
        data[32] = 0xc0; data[33] = 3; data[35] = (byte)name.Length;
        ushort nameHash = 0;
        foreach (var c in name.Select(char.ToUpperInvariant)) { nameHash = Rotate(nameHash); nameHash += (byte)c; nameHash = Rotate(nameHash); nameHash += (byte)(c >> 8); }
        U16(data, 36, nameHash); U64(data, 40, length); U32(data, 52, cluster); U64(data, 56, length);
        data[64] = 0xc1; Encoding.Unicode.GetBytes(name).CopyTo(data, 66);
        ushort checksum = 0;
        for (var i = 0; i < data.Length; i++) if (i is not (2 or 3)) { checksum = Rotate(checksum); checksum += data[i]; }
        U16(data, 2, checksum); return data;
    }
    private static uint Rotate(uint value) => (value >> 1) | (value << 31);
    private static ushort Rotate(ushort value) => (ushort)((value >> 1) | (value << 15));
    private static void U16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
    private static void U32(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)value);
    private static void U64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), (ulong)value);
}
