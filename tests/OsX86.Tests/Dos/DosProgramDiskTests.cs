using System.Buffers.Binary;
using System.Text;
using OsX86.Dos;
using OsX86.Interface;

namespace OsX86.Tests.Dos;

/// <summary>The program disk of a DOS run (<see cref="DosProgramDisk"/>) and its batch (<see cref="DosRunScript"/>), read back as DOS reads them.</summary>
[Trait("Category", "unit")]
public sealed class DosProgramDiskTests
{
    private static readonly DateTime Stamp = new(2026, 11, 16, 10, 30, 42);

    [Fact]
    public void The_disk_has_an_mbr_and_one_fat12_partition_at_sector_63()
    {
        byte[] disk = DosProgramDisk.Create([("HELLO.COM", new byte[] { 0xCD, 0x20 })], Stamp);

        Assert.Equal(0, disk.Length % (16 * 63 * 512));
        Assert.Equal(0x55, disk[510]);
        Assert.Equal(0xAA, disk[511]);
        Assert.Equal(0x01, disk[0x1BE + 4]);
        Assert.Equal(63u, BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(0x1BE + 8)));
        uint sectors = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(0x1BE + 12));
        Assert.True(63 + sectors <= disk.Length / 512);
        var fat = new Fat12(disk);
        Assert.Equal("FAT12   ", Encoding.ASCII.GetString(fat.Boot.AsSpan(54, 8)));
        Assert.Equal(sectors, fat.TotalSectors);
        Assert.True(fat.Clusters < 4085, "FAT12 by its cluster count, as DOS decides");
        Assert.Equal(0x55, fat.Boot[510]);
    }

    [Fact]
    public void Files_are_read_back_whole_through_their_cluster_chains()
    {
        byte[] program = new byte[70_000];
        new Random(11).NextBytes(program);
        byte[] batch = Encoding.ASCII.GetBytes(DosRunScript.Batch("TEST.EXE", "a b"));

        byte[] disk = DosProgramDisk.Create([("TEST.EXE", program), (DosRunScript.BatchFileName, batch), ("EMPTY.COM", Array.Empty<byte>())], Stamp);

        var fat = new Fat12(disk);
        Dictionary<string, (byte Attributes, ushort Date, byte[] Content)> files = fat.RootFiles();
        Assert.Equal(program, files["TEST.EXE"].Content);
        Assert.Equal(batch, files["OSX86RUN.BAT"].Content);
        Assert.Empty(files["EMPTY.COM"].Content);
        Assert.Equal(0x20, files["TEST.EXE"].Attributes);
        Assert.Equal(((2026 - 1980) << 9) | (11 << 5) | 16, files["TEST.EXE"].Date);
        Assert.Equal("OSX86 RUN  ", fat.VolumeLabel());
        Assert.True(fat.FreeClusters() * fat.ClusterBytes >= DosProgramDisk.FreeBytes, "room for what the program writes");
    }

    [Fact]
    public void A_large_program_takes_larger_clusters_and_stays_fat12()
    {
        byte[] program = new byte[8 * 1024 * 1024];
        program[^1] = 0x5A;

        var fat = new Fat12(DosProgramDisk.Create([("BIG.EXE", program)], Stamp));

        Assert.True(fat.SectorsPerCluster > 1);
        Assert.True(fat.Clusters < 4085);
        Assert.Equal(program, fat.RootFiles()["BIG.EXE"].Content);
    }

    [Fact]
    public void Wrong_files_are_refused()
    {
        Assert.Throws<ArgumentException>(() => DosProgramDisk.Create([], Stamp));
        Assert.Throws<ArgumentException>(() => DosProgramDisk.Create([("hello.com", new byte[1])], Stamp));
        Assert.Throws<ArgumentException>(() => DosProgramDisk.Create([("LONGNAME1.COM", new byte[1])], Stamp));
        Assert.Throws<ArgumentException>(() => DosProgramDisk.Create([("A.COM", new byte[1]), ("A.COM", new byte[1])], Stamp));
        Assert.Throws<ArgumentException>(() => DosProgramDisk.Create([("A.COM", new byte[DosProgramDisk.MaxContentBytes + 1])], Stamp));
    }

    [Fact]
    public void The_fat12_packing_puts_two_entries_in_three_bytes()
    {
        byte[] table = DosProgramDisk.PackFat12([0xFF8, 0xFFF, 0x003, 0xFFF], 6);

        Assert.Equal(new byte[] { 0xF8, 0xFF, 0xFF, 0x03, 0xF0, 0xFF }, table);
    }

    [Theory]
    [InlineData("hello.com", false, "HELLO.COM")]
    [InlineData("C:/games/My Prog.exe", true, "MYPROG.EXE")]
    [InlineData("dir\\averylongprogramname.com", false, "AVERYLON.COM")]
    [InlineData("tool", true, "TOOL.EXE")]
    [InlineData("tool.bin", false, "TOOL.COM")]
    [InlineData("été.com", false, "T.COM")]
    [InlineData("...com", false, "PROGRAM.COM")]
    [InlineData("con.com", false, "CON_.COM")]
    [InlineData("osx86run.com", false, "OSX86RU_.COM")]
    public void Short_names_are_8_3_in_upper_case(string fileName, bool isMz, string expected)
    {
        string name = DosRunScript.ToShortName(fileName, isMz);

        Assert.Equal(expected, name);
        Assert.True(DosRunScript.IsShortName(name));
    }

    [Fact]
    public void Arguments_become_a_dos_command_tail()
    {
        Assert.Equal(string.Empty, DosRunScript.FormatArguments([]));
        Assert.Equal("a /b \"c d\" 50%%", DosRunScript.FormatArguments(["a", "/b", "c d", "50%"]));
        Assert.Equal("\"\"", DosRunScript.FormatArguments([string.Empty]));
        foreach (string refused in new[] { "a|b", "a>b", "a<b", "a\"b", "é", "a\tb" })
        {
            Assert.Equal(X86ErrorCodes.ProgramNotSupported, Assert.Throws<X86Exception>(() => DosRunScript.FormatArguments([refused])).Code);
        }

        Assert.Throws<X86Exception>(() => DosRunScript.FormatArguments([new string('x', DosRunScript.MaxArgumentsLength + 1)]));
    }

    [Fact]
    public void The_batch_runs_the_program_between_its_two_markers_on_com1()
    {
        string batch = DosRunScript.Batch("TEST.COM", "x y");

        string[] lines = batch.Split("\r\n");
        Assert.Equal("@ECHO OFF", lines[0]);
        Assert.Contains("CLS", lines);
        Assert.Contains("ECHO OSX86-START >COM1", lines);
        Assert.Contains("D:\\TEST.COM x y", lines);
        Assert.Contains("ECHO OSX86-EXIT %ERRORLEVEL% >COM1", lines);
        Assert.Equal("PAUSE >NUL", lines[^2]);
        Assert.True(Array.IndexOf(lines, "ECHO OSX86-START >COM1") < Array.IndexOf(lines, "D:\\TEST.COM x y"));
        Assert.Equal("D:\\TEST.COM", DosRunScript.Batch("TEST.COM", string.Empty).Split("\r\n")[6]);
        Assert.Throws<ArgumentException>(() => DosRunScript.Batch("bad name", string.Empty));
    }

    [Fact]
    public void The_markers_of_com1_are_parsed()
    {
        Assert.True(DosRunScript.IsStart("OSX86-START "));
        Assert.False(DosRunScript.IsStart("OSX86-STARTED"));
        Assert.Equal(42, DosRunScript.ParseExit("OSX86-EXIT 42 "));
        Assert.Equal(0, DosRunScript.ParseExit("OSX86-EXIT 0"));
        Assert.Null(DosRunScript.ParseExit("OSX86-EXIT"));
        Assert.Null(DosRunScript.ParseExit("OSX86-EXIT -1"));
        Assert.Null(DosRunScript.ParseExit("OSX86-EXIT 300"));
        Assert.Null(DosRunScript.ParseExit("OSX86-EXIT x"));
        Assert.Null(DosRunScript.ParseExit("hello"));
    }

    /// <summary>A FAT12 reader of the partition at sector 63, the way DOS walks it.</summary>
    private sealed class Fat12
    {
        private readonly byte[] _partition;

        public Fat12(byte[] disk)
        {
            _partition = disk[(63 * 512)..];
            Boot = _partition[..512];
            SectorsPerCluster = Boot[13];
            ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(Boot.AsSpan(14));
            RootEntries = BinaryPrimitives.ReadUInt16LittleEndian(Boot.AsSpan(17));
            ushort small = BinaryPrimitives.ReadUInt16LittleEndian(Boot.AsSpan(19));
            TotalSectors = small != 0 ? small : BinaryPrimitives.ReadUInt32LittleEndian(Boot.AsSpan(32));
            FatSectors = BinaryPrimitives.ReadUInt16LittleEndian(Boot.AsSpan(22));
            FatStart = reserved;
            RootStart = reserved + (Boot[16] * FatSectors);
            DataStart = RootStart + (RootEntries * 32 / 512);
            Clusters = (int)((TotalSectors - DataStart) / SectorsPerCluster);
        }

        public byte[] Boot { get; }

        public int SectorsPerCluster { get; }

        public int RootEntries { get; }

        public uint TotalSectors { get; }

        public int FatSectors { get; }

        public int FatStart { get; }

        public int RootStart { get; }

        public int DataStart { get; }

        public int Clusters { get; }

        public int ClusterBytes => SectorsPerCluster * 512;

        public int Entry(int cluster)
        {
            int offset = (FatStart * 512) + (cluster * 3 / 2);
            int pair = _partition[offset] | (_partition[offset + 1] << 8);
            return (cluster & 1) == 0 ? pair & 0xFFF : pair >> 4;
        }

        public int FreeClusters() => Enumerable.Range(2, Clusters).Count(cluster => Entry(cluster) == 0);

        public string VolumeLabel()
        {
            byte[] entry = _partition[(RootStart * 512)..((RootStart * 512) + 32)];
            Assert.Equal(0x08, entry[11]);
            return Encoding.ASCII.GetString(entry, 0, 11);
        }

        public Dictionary<string, (byte Attributes, ushort Date, byte[] Content)> RootFiles()
        {
            var files = new Dictionary<string, (byte, ushort, byte[])>(StringComparer.Ordinal);
            for (int index = 0; index < RootEntries; index++)
            {
                int at = (RootStart * 512) + (index * 32);
                byte[] entry = _partition[at..(at + 32)];
                if (entry[0] == 0)
                {
                    break;
                }

                if ((entry[11] & 0x08) != 0)
                {
                    continue;
                }

                string name = Encoding.ASCII.GetString(entry, 0, 8).TrimEnd() + "." + Encoding.ASCII.GetString(entry, 8, 3).TrimEnd();
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(28));
                int cluster = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(26));
                var content = new List<byte>(size);
                while (content.Count < size)
                {
                    Assert.InRange(cluster, 2, Clusters + 1);
                    int start = (DataStart + ((cluster - 2) * SectorsPerCluster)) * 512;
                    content.AddRange(_partition.AsSpan(start, Math.Min(ClusterBytes, size - content.Count)).ToArray());
                    cluster = Entry(cluster);
                }

                Assert.True(size == 0 || cluster >= 0xFF8, $"{name}: the chain ends after its size");
                files[name] = (entry[11], BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(24)), [.. content]);
            }

            return files;
        }
    }
}
