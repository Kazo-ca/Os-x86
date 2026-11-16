namespace OsX86.Interface;

/// <summary>
/// The media types Os-x86 gives the programs it recognizes (decision 255 A: the names of
/// freedesktop.org's shared MIME database or of the IANA registry). The shell of Kazo routes a
/// program to the apps that declare an "execute" capability for its type (decisions 240 D, 252 A,
/// 253 A, ADR 0251 of Kazo).
/// </summary>
public static class ProgramMimeTypes
{
    /// <summary>A Windows program or library in the Portable Executable format (IANA).</summary>
    public const string PortableExecutable = "application/vnd.microsoft.portable-executable";

    /// <summary>A DOS program: an MZ executable without a newer header, or a <c>.com</c> image (freedesktop).</summary>
    public const string DosExecutable = "application/x-dosexec";

    /// <summary>A 16-bit Windows program in the New Executable format (freedesktop).</summary>
    public const string NewExecutable = "application/x-ms-ne-executable";

    /// <summary>A Linux program in the ELF format (freedesktop).</summary>
    public const string Executable = "application/x-executable";

    /// <summary>A batch file of <c>cmd.exe</c>: <c>.bat</c> or <c>.cmd</c> (freedesktop).</summary>
    public const string Batch = "application/x-bat";

    /// <summary>A Windows Installer package, <c>.msi</c> (freedesktop).</summary>
    public const string WindowsInstaller = "application/x-msi";

    /// <summary>Not a program Os-x86 knows.</summary>
    public const string Unknown = "application/octet-stream";
}

/// <summary>What kind of program a file is (decision 162 A).</summary>
public enum ProgramKind
{
    /// <summary>Not a program Os-x86 knows, or a header and an extension that contradict each other.</summary>
    Unknown = 0,

    /// <summary>A Windows program (Portable Executable, or New Executable for 16-bit Windows).</summary>
    Windows = 1,

    /// <summary>A DOS program (MZ without a newer header, or a <c>.com</c> image).</summary>
    Dos = 2,

    /// <summary>A Linux program (ELF).</summary>
    Linux = 3,

    /// <summary>A batch file of <c>cmd.exe</c>.</summary>
    Batch = 4,

    /// <summary>A Windows Installer package.</summary>
    WindowsInstaller = 5,
}

/// <summary>The subsystem a Windows program asks for in its header.</summary>
public enum ProgramSubsystem
{
    /// <summary>Not a Windows program, or not known from the bytes read.</summary>
    Unknown = 0,

    /// <summary>A console program: it runs in a terminal (decision 213 A).</summary>
    Console = 1,

    /// <summary>A windowed program: it needs a display.</summary>
    Windowed = 2,

    /// <summary>Another subsystem (native, EFI, ...).</summary>
    Other = 3,
}

/// <summary>What decided the result (decision 162 A: the header first, the extension as a fallback, a contradiction refused).</summary>
public enum ProgramDetectionSource
{
    /// <summary>Neither the header nor the extension says anything.</summary>
    None = 0,

    /// <summary>The first bytes of the file.</summary>
    Header = 1,

    /// <summary>The extension, the header being unknown.</summary>
    Extension = 2,

    /// <summary>The header and the extension contradict each other: the file is refused.</summary>
    Conflict = 3,
}

/// <summary>
/// The type of a program, as Os-x86 reads it from its first bytes and its extension
/// (<see cref="Ix86.DetectProgramAsync"/>).
/// </summary>
/// <param name="Kind">The kind of program.</param>
/// <param name="MimeType">Its media type (<see cref="ProgramMimeTypes"/>).</param>
/// <param name="Source">What decided it.</param>
/// <param name="Detail">A short English description (for example <c>PE32 console i386</c>, or why it is refused).</param>
public sealed record ProgramDetection(ProgramKind Kind, string MimeType, ProgramDetectionSource Source, string Detail)
{
    /// <summary>The width of the code: 16, 32 or 64, or <see langword="null"/> when not known.</summary>
    public int? Bits { get; init; }

    /// <summary>The subsystem of a Windows program.</summary>
    public ProgramSubsystem Subsystem { get; init; }

    /// <summary>Whether the program is a .NET assembly (a CLR header in its Portable Executable).</summary>
    public bool IsDotNet { get; init; }

    /// <summary>Whether the file is a library (a DLL, a shared object) rather than a program.</summary>
    public bool IsLibrary { get; init; }

    /// <summary>
    /// Whether the details come from the full headers. With the first 64 bytes only, an MZ file
    /// is told apart from a Portable Executable by its DOS header alone, and the subsystem, the
    /// width and .NET stay unknown.
    /// </summary>
    public bool IsComplete { get; init; }
}
