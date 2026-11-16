using System.Buffers.Binary;
using OsX86.Interface;

namespace OsX86.Programs;

/// <summary>
/// Tells what kind of program a file is from its first bytes and its extension (decisions 162 A and
/// 240 D: Os-x86 interprets the magic and the extension and gives a media type, 255 A). The rule:
/// the header first; the extension when the header says nothing; a header and an extension that
/// contradict each other (an ELF named <c>.exe</c>, an MZ named <c>.bat</c>) are refused.
/// <para>
/// Pure and stateless: no engine, no file system, no app context. It is shaped to become the format
/// detector the Feature declares once Kazo has ADR 0251 (decision 252 A: a named type the OS loads
/// without an app context and calls with the first <see cref="HeaderLength"/> bytes and the
/// extension). With those 64 bytes only, an MZ file is told apart from a newer executable by its DOS
/// header (relocation table at 0x40 and a new header past the DOS header, what every Windows linker
/// writes); with <see cref="FullLength"/> bytes the Portable Executable header itself is read: width,
/// subsystem, .NET, library.
/// </para>
/// </summary>
public sealed class ProgramDetector
{
    /// <summary>The bytes the format detector of ADR 0251 receives.</summary>
    public const int HeaderLength = 64;

    /// <summary>The bytes that hold the headers of nearly every program; what <c>x86 detect</c> and <c>x86 run</c> read.</summary>
    public const int FullLength = 4096;

    /// <summary>The name under which the Feature declares it (decision 252 A).</summary>
    public const string DetectorName = "ca.kazo.x86.programs";

    private const ushort MachineI386 = 0x014c;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xaa64;
    private const ushort MachineArmNt = 0x01c4;
    private const ushort Pe32Magic = 0x10b;
    private const ushort Pe32PlusMagic = 0x20b;
    private const ushort FileDll = 0x2000;
    private const int ClrDirectory = 14;

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>The detector's call for the shell (decision 252 A); see <see cref="Detect"/>.</summary>
    public ValueTask<ProgramDetection> DetectAsync(ReadOnlyMemory<byte> header, string? extension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Detect(header.Span, extension));
    }

    /// <summary>The extension of a file name, lowercase with its dot (<c>.exe</c>), or <see langword="null"/>.</summary>
    public static string? ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        int slash = Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\'));
        string name = fileName[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..].ToLowerInvariant() : null;
    }

    /// <summary>The type of a program.</summary>
    /// <param name="header">The first bytes of the file: <see cref="HeaderLength"/> at least for a useful answer, <see cref="FullLength"/> for the details.</param>
    /// <param name="extension">The extension, with or without its dot, any case; or <see langword="null"/>.</param>
    public static ProgramDetection Detect(ReadOnlySpan<byte> header, string? extension)
    {
        string? ext = NormalizeExtension(extension);
        ProgramDetection? byHeader = FromHeader(header);
        if (byHeader is not null)
        {
            return Contradiction(byHeader.Kind, ext) is { } why ? Conflict(why) : byHeader;
        }

        return FromExtension(ext, header);
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        string trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private static ProgramDetection Conflict(string why) =>
        new(ProgramKind.Unknown, ProgramMimeTypes.Unknown, ProgramDetectionSource.Conflict, why);

    private static ProgramDetection? FromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
        {
            return FromMz(header);
        }

        if (header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        {
            return FromElf(header);
        }

        return null;
    }

    /// <summary>The extension decides, the header being unknown; <c>.exe</c> and <c>.msi</c> need theirs.</summary>
    private static ProgramDetection FromExtension(string? ext, ReadOnlySpan<byte> header) => ext switch
    {
        ".bat" or ".cmd" => new(ProgramKind.Batch, ProgramMimeTypes.Batch, ProgramDetectionSource.Extension, "batch file of cmd.exe") { IsComplete = true },
        ".com" => new(ProgramKind.Dos, ProgramMimeTypes.DosExecutable, ProgramDetectionSource.Extension, "DOS .com image") { Bits = 16, IsComplete = true },
        ".msi" when header.StartsWith(OleSignature) => new(ProgramKind.WindowsInstaller, ProgramMimeTypes.WindowsInstaller, ProgramDetectionSource.Header, "Windows Installer package") { IsComplete = true },
        ".msi" => Conflict("an .msi file without the signature of a Windows Installer package"),
        ".exe" => Conflict("an .exe file without the MZ signature"),
        _ => new(ProgramKind.Unknown, ProgramMimeTypes.Unknown, ProgramDetectionSource.None, "not a program Os-x86 knows"),
    };

    /// <summary>Why a recognized header and the extension disagree, or <see langword="null"/>.</summary>
    private static string? Contradiction(ProgramKind byHeader, string? ext) => (byHeader, ext) switch
    {
        (ProgramKind.Linux, ".exe" or ".com" or ".dll" or ".bat" or ".cmd" or ".msi") => $"an ELF program named {ext}",
        (ProgramKind.Windows or ProgramKind.Dos, ".bat" or ".cmd" or ".msi") => $"an MZ executable named {ext}",
        _ => null,
    };

    private static ProgramDetection FromMz(ReadOnlySpan<byte> header)
    {
        const string dos = "DOS MZ executable";
        if (header.Length < HeaderLength)
        {
            return new(ProgramKind.Dos, ProgramMimeTypes.DosExecutable, ProgramDetectionSource.Header, dos + " (header too short to look for a newer one)") { Bits = 16 };
        }

        ushort relocations = BinaryPrimitives.ReadUInt16LittleEndian(header[0x18..]);
        uint newHeader = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
        bool looksNew = relocations >= 0x40 && newHeader >= HeaderLength && newHeader < 0x1000_0000;
        if (!looksNew)
        {
            return new(ProgramKind.Dos, ProgramMimeTypes.DosExecutable, ProgramDetectionSource.Header, dos) { Bits = 16, IsComplete = true };
        }

        if (header.Length < newHeader + 4)
        {
            // The 64 bytes of ADR 0251: a new executable, almost always a Portable Executable.
            return new(ProgramKind.Windows, ProgramMimeTypes.PortableExecutable, ProgramDetectionSource.Header, "Windows executable (details need more of the file)");
        }

        ReadOnlySpan<byte> signature = header.Slice((int)newHeader, 4);
        if (signature.SequenceEqual("PE\0\0"u8))
        {
            return FromPe(header, (int)newHeader);
        }

        if (signature[..2].SequenceEqual("NE"u8))
        {
            return new(ProgramKind.Windows, ProgramMimeTypes.NewExecutable, ProgramDetectionSource.Header, "16-bit Windows NE executable") { Bits = 16, IsComplete = true };
        }

        // LE and LX (DOS extenders, OS/2) and anything else after a DOS header start under DOS.
        return new(ProgramKind.Dos, ProgramMimeTypes.DosExecutable, ProgramDetectionSource.Header, dos + " with an unknown newer header") { Bits = 16, IsComplete = true };
    }

    private static ProgramDetection FromPe(ReadOnlySpan<byte> header, int pe)
    {
        int coff = pe + 4;
        int optional = coff + 20;
        if (header.Length < optional + 2)
        {
            return new(ProgramKind.Windows, ProgramMimeTypes.PortableExecutable, ProgramDetectionSource.Header, "Portable Executable (details need more of the file)");
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(header[coff..]);
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header[(coff + 18)..]);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header[optional..]);
        int? bits = magic switch { Pe32Magic => 32, Pe32PlusMagic => 64, _ => null };
        bool library = (characteristics & FileDll) != 0;
        ProgramSubsystem subsystem = ProgramSubsystem.Unknown;
        bool dotNet = false;
        bool complete = false;
        int countOffset = optional + (bits == 32 ? 92 : 108);
        if (bits is not null && header.Length >= countOffset + 4)
        {
            subsystem = BinaryPrimitives.ReadUInt16LittleEndian(header[(optional + 68)..]) switch
            {
                2 => ProgramSubsystem.Windowed,
                3 => ProgramSubsystem.Console,
                _ => ProgramSubsystem.Other,
            };
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[countOffset..]);
            int clr = countOffset + 4 + (ClrDirectory * 8);
            if (count <= ClrDirectory)
            {
                complete = true;
            }
            else if (header.Length >= clr + 8)
            {
                dotNet = BinaryPrimitives.ReadUInt32LittleEndian(header[clr..]) != 0 && BinaryPrimitives.ReadUInt32LittleEndian(header[(clr + 4)..]) != 0;
                complete = true;
            }
        }

        string width = bits switch { 32 => "PE32", 64 => "PE32+", _ => "PE" };
        string role = library ? "library" : subsystem switch
        {
            ProgramSubsystem.Console => "console",
            ProgramSubsystem.Windowed => "windowed",
            ProgramSubsystem.Other => "other subsystem",
            _ => "program",
        };
        string detail = $"{width} {role} {MachineName(machine)}{(dotNet ? " .NET" : string.Empty)}";
        return new(ProgramKind.Windows, ProgramMimeTypes.PortableExecutable, ProgramDetectionSource.Header, detail)
        {
            Bits = bits,
            Subsystem = subsystem,
            IsDotNet = dotNet,
            IsLibrary = library,
            IsComplete = complete,
        };
    }

    private static string MachineName(ushort machine) => machine switch
    {
        MachineI386 => "i386",
        MachineAmd64 => "x86-64",
        MachineArm64 => "arm64",
        MachineArmNt => "arm",
        _ => $"machine 0x{machine:x4}",
    };

    private static ProgramDetection FromElf(ReadOnlySpan<byte> header)
    {
        if (header.Length < 20)
        {
            return new(ProgramKind.Linux, ProgramMimeTypes.Executable, ProgramDetectionSource.Header, "ELF file (header too short)");
        }

        int? bits = header[4] switch { 1 => 32, 2 => 64, _ => null };
        bool little = header[5] != 2;
        ushort type = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) : BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        ushort machine = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) : BinaryPrimitives.ReadUInt16BigEndian(header[18..]);
        string machineName = machine switch { 3 => "i386", 62 => "x86-64", 40 => "arm", 183 => "arm64", _ => $"machine {machine}" };
        string typeName = type switch { 2 => "program", 3 => "position-independent program or shared library", 1 => "object file", 4 => "core dump", _ => "file" };
        return new(ProgramKind.Linux, ProgramMimeTypes.Executable, ProgramDetectionSource.Header, $"ELF{bits} {typeName} {machineName}")
        {
            Bits = bits,
            IsLibrary = type is not (2 or 3),
            IsComplete = true,
        };
    }
}
