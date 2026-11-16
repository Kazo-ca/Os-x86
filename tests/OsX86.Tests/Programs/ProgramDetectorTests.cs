using System.Text;
using OsX86.Interface;
using OsX86.Programs;

namespace OsX86.Tests.Programs;

/// <summary>The detection of decisions 162 A, 240 D and 252 to 255 A, on headers built in the tests.</summary>
[Trait("Category", "unit")]
public sealed class ProgramDetectorTests
{
    [Fact]
    public void A_console_PE32_for_i386_is_a_Windows_console_program()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Pe(), ".exe");

        Assert.Equal(ProgramKind.Windows, result.Kind);
        Assert.Equal(ProgramMimeTypes.PortableExecutable, result.MimeType);
        Assert.Equal(ProgramDetectionSource.Header, result.Source);
        Assert.Equal(32, result.Bits);
        Assert.Equal(ProgramSubsystem.Console, result.Subsystem);
        Assert.False(result.IsDotNet);
        Assert.False(result.IsLibrary);
        Assert.True(result.IsComplete);
        Assert.Equal("PE32 console i386", result.Detail);
    }

    [Fact]
    public void A_windowed_PE32_plus_for_x86_64_says_so()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Pe(pe32Plus: true, subsystem: 2, machine: 0x8664), "EXE");

        Assert.Equal(64, result.Bits);
        Assert.Equal(ProgramSubsystem.Windowed, result.Subsystem);
        Assert.Equal("PE32+ windowed x86-64", result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_CLR_header_makes_a_dotnet_program(bool pe32Plus)
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Pe(pe32Plus: pe32Plus, dotNet: true), ".exe");

        Assert.True(result.IsDotNet);
        Assert.True(result.IsComplete);
        Assert.EndsWith(".NET", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dll_is_a_library()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Pe(dll: true), ".dll");

        Assert.True(result.IsLibrary);
        Assert.Equal("PE32 library i386", result.Detail);
    }

    [Theory]
    [InlineData((ushort)0xaa64, "arm64")]
    [InlineData((ushort)0x01c4, "arm")]
    [InlineData((ushort)0x1234, "machine 0x1234")]
    public void The_machine_is_named(ushort machine, string name)
    {
        Assert.EndsWith(name, ProgramDetector.Detect(TestPrograms.Pe(machine: machine), null).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((ushort)1, ProgramSubsystem.Other)]
    [InlineData((ushort)10, ProgramSubsystem.Other)]
    public void Other_subsystems_are_other(ushort subsystem, ProgramSubsystem expected)
    {
        Assert.Equal(expected, ProgramDetector.Detect(TestPrograms.Pe(subsystem: subsystem), ".exe").Subsystem);
    }

    [Fact]
    public void Few_data_directories_make_a_complete_answer_without_CLR()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Pe(directories: 10), ".exe");

        Assert.True(result.IsComplete);
        Assert.False(result.IsDotNet);
    }

    [Fact]
    public void The_64_bytes_of_ADR_0251_tell_a_Windows_program_by_its_DOS_header()
    {
        byte[] header = TestPrograms.Pe()[..ProgramDetector.HeaderLength];

        ProgramDetection result = ProgramDetector.Detect(header, ".exe");

        Assert.Equal(ProgramKind.Windows, result.Kind);
        Assert.Equal(ProgramMimeTypes.PortableExecutable, result.MimeType);
        Assert.False(result.IsComplete);
        Assert.Null(result.Bits);
        Assert.Equal(ProgramSubsystem.Unknown, result.Subsystem);
    }

    [Fact]
    public void A_PE_header_cut_before_its_optional_header_stays_incomplete()
    {
        byte[] header = TestPrograms.Pe()[..(TestPrograms.PeOffset + 10)];

        ProgramDetection result = ProgramDetector.Detect(header, ".exe");

        Assert.Equal(ProgramKind.Windows, result.Kind);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void A_PE_header_cut_before_the_CLR_directory_stays_incomplete()
    {
        byte[] header = TestPrograms.Pe()[..(TestPrograms.PeOffset + 24 + 100)];

        ProgramDetection result = ProgramDetector.Detect(header, ".exe");

        Assert.Equal(ProgramSubsystem.Console, result.Subsystem);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void An_MZ_without_newer_header_is_a_DOS_program()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Dos(), ".exe");

        Assert.Equal(ProgramKind.Dos, result.Kind);
        Assert.Equal(ProgramMimeTypes.DosExecutable, result.MimeType);
        Assert.Equal(16, result.Bits);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void An_MZ_shorter_than_64_bytes_is_a_DOS_program_not_fully_known()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Dos()[..20], ".exe");

        Assert.Equal(ProgramKind.Dos, result.Kind);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void An_MZ_whose_new_header_is_neither_PE_nor_NE_is_a_DOS_program()
    {
        byte[] bytes = TestPrograms.Pe();
        "LE\0\0"u8.CopyTo(bytes.AsSpan(TestPrograms.PeOffset));

        ProgramDetection result = ProgramDetector.Detect(bytes, ".exe");

        Assert.Equal(ProgramKind.Dos, result.Kind);
        Assert.Contains("unknown newer header", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_New_Executable_is_a_16_bit_Windows_program()
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Ne(), ".exe");

        Assert.Equal(ProgramKind.Windows, result.Kind);
        Assert.Equal(ProgramMimeTypes.NewExecutable, result.MimeType);
        Assert.Equal(16, result.Bits);
    }

    [Fact]
    public void A_PE_with_an_unknown_optional_magic_has_no_width()
    {
        byte[] bytes = TestPrograms.Pe();
        bytes[TestPrograms.PeOffset + 24] = 0x07;

        ProgramDetection result = ProgramDetector.Detect(bytes, ".exe");

        Assert.Null(result.Bits);
        Assert.StartsWith("PE program", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(32, (ushort)2, (ushort)3, "ELF32 program i386", false)]
    [InlineData(64, (ushort)3, (ushort)62, "ELF64 position-independent program or shared library x86-64", false)]
    [InlineData(32, (ushort)1, (ushort)40, "ELF32 object file arm", true)]
    [InlineData(64, (ushort)4, (ushort)183, "ELF64 core dump arm64", true)]
    [InlineData(32, (ushort)9, (ushort)99, "ELF32 file machine 99", true)]
    public void An_ELF_file_is_a_Linux_program(int bits, ushort type, ushort machine, string detail, bool library)
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Elf(bits, type, machine), null);

        Assert.Equal(ProgramKind.Linux, result.Kind);
        Assert.Equal(ProgramMimeTypes.Executable, result.MimeType);
        Assert.Equal(bits, result.Bits);
        Assert.Equal(detail, result.Detail);
        Assert.Equal(library, result.IsLibrary);
    }

    [Fact]
    public void A_big_endian_ELF_is_read_as_such()
    {
        byte[] bytes = TestPrograms.Elf();
        bytes[5] = 2;
        bytes[16] = 0;
        bytes[17] = 2;
        bytes[18] = 0;
        bytes[19] = 3;

        Assert.Equal("ELF32 program i386", ProgramDetector.Detect(bytes, null).Detail);
    }

    [Fact]
    public void A_short_ELF_header_is_still_Linux()
    {
        Assert.Equal(ProgramKind.Linux, ProgramDetector.Detect(TestPrograms.Elf()[..8], null).Kind);
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".CMD")]
    [InlineData("bat")]
    public void A_batch_file_is_known_by_its_extension(string extension)
    {
        ProgramDetection result = ProgramDetector.Detect(Encoding.ASCII.GetBytes("@echo off\r\necho hi\r\n"), extension);

        Assert.Equal(ProgramKind.Batch, result.Kind);
        Assert.Equal(ProgramMimeTypes.Batch, result.MimeType);
        Assert.Equal(ProgramDetectionSource.Extension, result.Source);
    }

    [Fact]
    public void A_com_image_is_a_DOS_program_by_its_extension()
    {
        ProgramDetection result = ProgramDetector.Detect([0xB4, 0x09, 0xBA, 0x08, 0x01, 0xCD, 0x21], ".com");

        Assert.Equal(ProgramKind.Dos, result.Kind);
        Assert.Equal(ProgramDetectionSource.Extension, result.Source);
    }

    [Fact]
    public void A_com_file_with_an_MZ_header_is_read_by_its_header()
    {
        Assert.Equal(ProgramKind.Windows, ProgramDetector.Detect(TestPrograms.Pe(), ".com").Kind);
    }

    [Fact]
    public void An_msi_package_needs_its_OLE_signature()
    {
        ProgramDetection package = ProgramDetector.Detect(TestPrograms.Ole(), ".msi");
        ProgramDetection notOne = ProgramDetector.Detect(Encoding.ASCII.GetBytes("plain text"), ".msi");

        Assert.Equal(ProgramKind.WindowsInstaller, package.Kind);
        Assert.Equal(ProgramMimeTypes.WindowsInstaller, package.MimeType);
        Assert.Equal(ProgramDetectionSource.Conflict, notOne.Source);
    }

    [Fact]
    public void An_OLE_file_without_the_msi_extension_is_not_a_program()
    {
        Assert.Equal(ProgramKind.Unknown, ProgramDetector.Detect(TestPrograms.Ole(), ".doc").Kind);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".dll")]
    [InlineData(".bat")]
    [InlineData(".msi")]
    public void An_ELF_named_like_a_Windows_file_is_refused(string extension)
    {
        ProgramDetection result = ProgramDetector.Detect(TestPrograms.Elf(), extension);

        Assert.Equal(ProgramKind.Unknown, result.Kind);
        Assert.Equal(ProgramDetectionSource.Conflict, result.Source);
        Assert.Equal(ProgramMimeTypes.Unknown, result.MimeType);
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".msi")]
    public void An_MZ_named_like_a_script_or_a_package_is_refused(string extension)
    {
        Assert.Equal(ProgramDetectionSource.Conflict, ProgramDetector.Detect(TestPrograms.Pe(), extension).Source);
    }

    [Fact]
    public void An_exe_without_the_MZ_signature_is_refused()
    {
        Assert.Equal(ProgramDetectionSource.Conflict, ProgramDetector.Detect(Encoding.ASCII.GetBytes("not a program"), ".exe").Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".txt")]
    public void Unknown_bytes_without_a_program_extension_are_unknown(string? extension)
    {
        ProgramDetection result = ProgramDetector.Detect(Encoding.ASCII.GetBytes("hello"), extension);

        Assert.Equal(ProgramKind.Unknown, result.Kind);
        Assert.Equal(ProgramDetectionSource.None, result.Source);
    }

    [Fact]
    public void Nothing_at_all_is_unknown()
    {
        Assert.Equal(ProgramKind.Unknown, ProgramDetector.Detect([], null).Kind);
    }

    [Fact]
    public void The_header_wins_over_an_unknown_extension()
    {
        Assert.Equal(ProgramKind.Windows, ProgramDetector.Detect(TestPrograms.Pe(), ".bin").Kind);
        Assert.Equal(ProgramKind.Linux, ProgramDetector.Detect(TestPrograms.Elf(), ".exe.txt").Kind);
    }

    [Theory]
    [InlineData("hello.exe", ".exe")]
    [InlineData("dir/Setup.MSI", ".msi")]
    [InlineData(@"C:\a.b\run.BAT", ".bat")]
    [InlineData("noext", null)]
    [InlineData(".profile", null)]
    [InlineData("trailing.", null)]
    [InlineData(null, null)]
    [InlineData("dir.d/file", null)]
    public void The_extension_of_a_name_is_lowercase_with_its_dot(string? name, string? expected)
    {
        Assert.Equal(expected, ProgramDetector.ExtensionOf(name));
    }

    [Fact]
    public async Task The_detector_of_ADR_0251_answers_like_Detect_and_has_a_name()
    {
        var detector = new ProgramDetector();

        ProgramDetection result = await detector.DetectAsync(TestPrograms.Pe()[..64], ".exe", CancellationToken.None);

        Assert.Equal(ProgramMimeTypes.PortableExecutable, result.MimeType);
        Assert.Equal("ca.kazo.x86.programs", ProgramDetector.DetectorName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await detector.DetectAsync(Array.Empty<byte>(), null, new CancellationToken(true)));
    }
}
