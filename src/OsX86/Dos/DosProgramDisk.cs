using System.Buffers.Binary;
using System.Text;

namespace OsX86.Dos;

/// <summary>
/// The program disk of a DOS run (work item 11): a small hard disk the VM gets as <c>hdb</c>, made
/// here and sent with the start options, never stored (its writes stay in memory and vanish with
/// the run). An MBR with one FAT12 primary partition (type 0x01) at sector 63, which FreeDOS mounts
/// as <c>D:</c> (the FreeDOS disk <c>hda</c> has one partition, <c>C:</c>), holding the files given in
/// the root directory. FAT12 because a program disk is small (at most <see cref="MaxContentBytes"/>);
/// the geometry is the one v86 gives its disks (16 heads, 63 sectors per track).
/// No agent is involved (212 A): the guest only reads a disk.
/// </summary>
public static class DosProgramDisk
{
    /// <summary>The bytes of a sector.</summary>
    public const int SectorSize = 512;

    /// <summary>Where the partition starts (the first track is the MBR's).</summary>
    public const int PartitionStart = 63;

    /// <summary>The largest total of file bytes a program disk holds.</summary>
    public const int MaxContentBytes = 16 * 1024 * 1024;

    /// <summary>The free space left on the disk for what the program writes next to itself.</summary>
    public const int FreeBytes = 1024 * 1024;

    /// <summary>The volume label of the disk.</summary>
    public const string VolumeLabel = "OSX86 RUN";

    private const int Heads = 16;
    private const int SectorsPerTrack = 63;
    private const int RootEntries = 512;
    private const int MaxFat12Clusters = 4084;
    private const byte Media = 0xF8;
    private const byte PartitionTypeFat12 = 0x01;
    private const int DirectoryEntrySize = 32;

    /// <summary>
    /// Makes the disk image holding <paramref name="files"/> in its root directory, with the date
    /// <paramref name="stamp"/> on every file.
    /// </summary>
    /// <param name="files">Each file's 8.3 name (upper case, as <see cref="DosRunScript.ToShortName"/> makes it) and content.</param>
    /// <param name="stamp">The date and time written on the files.</param>
    /// <exception cref="ArgumentException">A name is not 8.3, appears twice, or the files are more than <see cref="MaxContentBytes"/>.</exception>
    public static byte[] Create(IReadOnlyList<(string Name, ReadOnlyMemory<byte> Content)> files, DateTime stamp)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is 0 or > RootEntries - 1)
        {
            throw new ArgumentException($"A program disk holds 1 to {RootEntries - 1} files.", nameof(files));
        }

        long total = files.Sum(static file => (long)file.Content.Length);
        if (total > MaxContentBytes)
        {
            throw new ArgumentException($"The files are {total} bytes; a program disk holds at most {MaxContentBytes}.", nameof(files));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, _) in files)
        {
            if (!DosRunScript.IsShortName(name) || !names.Add(name))
            {
                throw new ArgumentException($"'{name}' is not an 8.3 name in upper case, or it is given twice.", nameof(files));
            }
        }

        Layout layout = Layout.For(files);
        byte[] disk = new byte[(long)layout.DiskSectors * SectorSize];
        WriteMbr(disk, layout);
        Span<byte> partition = disk.AsSpan(PartitionStart * SectorSize);
        WriteBootSector(partition, layout);
        WriteFilesAndTables(partition, layout, files, stamp);
        return disk;
    }

    /// <summary>The sizes of the file system, from the clusters the files need.</summary>
    private sealed record Layout(int SectorsPerCluster, int Clusters, int FatSectors, int PartitionSectors, int DiskSectors)
    {
        public const int RootSectors = RootEntries * DirectoryEntrySize / SectorSize;

        public int ClusterBytes => SectorsPerCluster * SectorSize;

        public int FirstFatSector => 1;

        public int RootSector => 1 + (2 * FatSectors);

        public int DataSector => RootSector + RootSectors;

        public static Layout For(IReadOnlyList<(string Name, ReadOnlyMemory<byte> Content)> files)
        {
            for (int sectorsPerCluster = 1; sectorsPerCluster <= 64; sectorsPerCluster *= 2)
            {
                int clusterBytes = sectorsPerCluster * SectorSize;
                long needed = files.Sum(file => (file.Content.Length + (long)clusterBytes - 1) / clusterBytes) + ((FreeBytes + (long)clusterBytes - 1) / clusterBytes);
                if (needed > MaxFat12Clusters)
                {
                    continue;
                }

                int clusters = (int)needed;
                int fatSectors = (((clusters + 2) * 3 / 2) + SectorSize) / SectorSize;
                int partitionSectors = 1 + (2 * fatSectors) + RootSectors + (clusters * sectorsPerCluster);
                int cylinder = Heads * SectorsPerTrack;
                int diskSectors = (PartitionStart + partitionSectors + cylinder - 1) / cylinder * cylinder;
                return new Layout(sectorsPerCluster, clusters, fatSectors, partitionSectors, diskSectors);
            }

            throw new ArgumentException("The files do not fit in a FAT12 program disk.", nameof(files));
        }
    }

    private static void WriteMbr(byte[] disk, Layout layout)
    {
        Span<byte> mbr = disk.AsSpan(0, SectorSize);
        mbr[0] = 0xCD; // int 18h: this disk is not bootable, the BIOS goes on
        mbr[1] = 0x18;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr[0x1B8..], 0x4F535836); // disk signature "OSX6"
        Span<byte> entry = mbr.Slice(0x1BE, 16);
        entry[0] = 0x00;
        WriteChs(entry[1..4], PartitionStart);
        entry[4] = PartitionTypeFat12;
        WriteChs(entry[5..8], PartitionStart + layout.PartitionSectors - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], PartitionStart);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)layout.PartitionSectors);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
    }

    /// <summary>Head, sector (with the cylinder's high bits) and cylinder of a sector, as the MBR keeps them.</summary>
    private static void WriteChs(Span<byte> chs, int lba)
    {
        int cylinder = lba / (Heads * SectorsPerTrack);
        int head = lba / SectorsPerTrack % Heads;
        int sector = (lba % SectorsPerTrack) + 1;
        if (cylinder > 1023)
        {
            (cylinder, head, sector) = (1023, Heads - 1, SectorsPerTrack);
        }

        chs[0] = (byte)head;
        chs[1] = (byte)(sector | ((cylinder >> 2) & 0xC0));
        chs[2] = (byte)cylinder;
    }

    private static void WriteBootSector(Span<byte> boot, Layout layout)
    {
        boot[0] = 0xEB; // jmp short to the code, nop
        boot[1] = 0x3C;
        boot[2] = 0x90;
        Encoding.ASCII.GetBytes("OSX86   ").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], SectorSize);
        boot[13] = (byte)layout.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], 1); // reserved sectors: the boot sector
        boot[16] = 2; // FAT copies
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], RootEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], layout.PartitionSectors < 65536 ? (ushort)layout.PartitionSectors : (ushort)0);
        boot[21] = Media;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], (ushort)layout.FatSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[24..], SectorsPerTrack);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[26..], Heads);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[28..], PartitionStart); // hidden sectors
        BinaryPrimitives.WriteUInt32LittleEndian(boot[32..], layout.PartitionSectors < 65536 ? 0u : (uint)layout.PartitionSectors);
        boot[36] = 0x80; // the second hard disk is still a hard disk for DOS
        boot[38] = 0x29; // extended boot signature: the three fields below are valid
        BinaryPrimitives.WriteUInt32LittleEndian(boot[39..], 0x0B5D0586);
        Encoding.ASCII.GetBytes(VolumeLabel.PadRight(11)).CopyTo(boot[43..]);
        Encoding.ASCII.GetBytes("FAT12   ").CopyTo(boot[54..]);
        boot[62] = 0xCD; // int 18h if anything ever jumps here
        boot[63] = 0x18;
        boot[510] = 0x55;
        boot[511] = 0xAA;
    }

    private static void WriteFilesAndTables(Span<byte> partition, Layout layout, IReadOnlyList<(string Name, ReadOnlyMemory<byte> Content)> files, DateTime stamp)
    {
        int[] fat = new int[layout.Clusters + 2];
        fat[0] = 0xF00 | Media;
        fat[1] = 0xFFF;
        Span<byte> root = partition.Slice(layout.RootSector * SectorSize, Layout.RootSectors * SectorSize);
        WriteDirectoryEntry(root[..DirectoryEntrySize], VolumeLabel.PadRight(11), 0x08, 0, 0, stamp);
        int nextCluster = 2;
        for (int index = 0; index < files.Count; index++)
        {
            (string name, ReadOnlyMemory<byte> content) = files[index];
            int count = (content.Length + layout.ClusterBytes - 1) / layout.ClusterBytes;
            int first = count == 0 ? 0 : nextCluster;
            for (int cluster = 0; cluster < count; cluster++)
            {
                int current = nextCluster + cluster;
                fat[current] = cluster == count - 1 ? 0xFFF : current + 1;
            }

            if (count > 0)
            {
                content.Span.CopyTo(partition[((layout.DataSector + ((first - 2) * layout.SectorsPerCluster)) * SectorSize)..]);
            }

            nextCluster += count;
            string[] parts = name.Split('.');
            string padded = parts[0].PadRight(8) + (parts.Length > 1 ? parts[1] : string.Empty).PadRight(3);
            WriteDirectoryEntry(root.Slice((index + 1) * DirectoryEntrySize, DirectoryEntrySize), padded, 0x20, first, content.Length, stamp);
        }

        byte[] table = PackFat12(fat, layout.FatSectors * SectorSize);
        table.CopyTo(partition[(layout.FirstFatSector * SectorSize)..]);
        table.CopyTo(partition[((layout.FirstFatSector + layout.FatSectors) * SectorSize)..]);
    }

    /// <summary>Twelve bits per cluster, two clusters in three bytes.</summary>
    internal static byte[] PackFat12(IReadOnlyList<int> entries, int length)
    {
        byte[] table = new byte[length];
        for (int cluster = 0; cluster < entries.Count; cluster++)
        {
            int offset = cluster * 3 / 2;
            int value = entries[cluster] & 0xFFF;
            if ((cluster & 1) == 0)
            {
                table[offset] = (byte)value;
                table[offset + 1] = (byte)((table[offset + 1] & 0xF0) | (value >> 8));
            }
            else
            {
                table[offset] = (byte)((table[offset] & 0x0F) | ((value & 0x0F) << 4));
                table[offset + 1] = (byte)(value >> 4);
            }
        }

        return table;
    }

    private static void WriteDirectoryEntry(Span<byte> entry, string paddedName, byte attributes, int firstCluster, int size, DateTime stamp)
    {
        Encoding.ASCII.GetBytes(paddedName).CopyTo(entry);
        entry[11] = attributes;
        ushort time = (ushort)((stamp.Hour << 11) | (stamp.Minute << 5) | (stamp.Second / 2));
        ushort date = (ushort)(((Math.Clamp(stamp.Year, 1980, 2107) - 1980) << 9) | (stamp.Month << 5) | stamp.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[22..], time);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[24..], date);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)firstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], (uint)size);
    }
}
