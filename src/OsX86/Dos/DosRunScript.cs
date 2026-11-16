using System.Globalization;
using System.Text;
using OsX86.Interface;

namespace OsX86.Dos;

/// <summary>
/// How a DOS program is started and how its end is known, without an agent (212 A, work item 11).
/// The program disk (<see cref="DosProgramDisk"/>) holds the program under an 8.3 name and
/// <see cref="BatchFileName"/>, which the <c>FDAUTO.BAT</c> of the FreeDOS image calls when it finds
/// it on <c>D:</c>. The batch clears the screen, writes <see cref="StartMarker"/> on COM1 (the
/// serial port, never on the screen), runs the program, writes <see cref="ExitMarker"/> and the
/// program's <c>ERRORLEVEL</c> on COM1, then waits for a key that never comes (<c>PAUSE</c>, idle with
/// <c>FDAPM APMDOS</c>) so that the last screen of the program stays as it left it until the VM stops.
/// </summary>
public static class DosRunScript
{
    /// <summary>The batch file <c>FDAUTO.BAT</c> runs when the program disk is there.</summary>
    public const string BatchFileName = "OSX86RUN.BAT";

    /// <summary>The line the batch writes on COM1 just before the program starts.</summary>
    public const string StartMarker = "OSX86-START";

    /// <summary>The line the batch writes on COM1 when the program ended, followed by its exit code.</summary>
    public const string ExitMarker = "OSX86-EXIT";

    /// <summary>The longest command tail DOS passes to a program (127 bytes with its leading blank and the CR).</summary>
    public const int MaxArgumentsLength = 125;

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3"];

    /// <summary>
    /// The 8.3 name of a program on the program disk: the last part of <paramref name="fileName"/>, in
    /// upper case, without the characters DOS refuses, cut to 8 characters; the extension <c>.COM</c>
    /// or <c>.EXE</c> it has, otherwise <c>.EXE</c> for an MZ file and <c>.COM</c> for the rest.
    /// </summary>
    public static string ToShortName(string fileName, bool isMz)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        string extension = dot >= 0 ? name[(dot + 1)..].ToUpperInvariant() : string.Empty;
        string stem = dot >= 0 ? name[..dot] : name;
        var builder = new StringBuilder(8);
        foreach (char c in stem.ToUpperInvariant())
        {
            if (builder.Length < 8 && c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')
            {
                builder.Append(c);
            }
        }

        string shortStem = builder.Length == 0 ? "PROGRAM" : builder.ToString();
        if (DeviceNames.Contains(shortStem) || shortStem == Path.GetFileNameWithoutExtension(BatchFileName))
        {
            shortStem = shortStem.Length < 8 ? shortStem + "_" : shortStem[..7] + "_";
        }

        string shortExtension = extension is "COM" or "EXE" ? extension : isMz ? "EXE" : "COM";
        return $"{shortStem}.{shortExtension}";
    }

    /// <summary>Whether <paramref name="name"/> is an upper-case 8.3 name of letters, digits, <c>_</c> and <c>-</c>.</summary>
    public static bool IsShortName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string[] parts = name.Split('.');
        return parts.Length is 1 or 2
            && parts[0].Length is >= 1 and <= 8
            && (parts.Length == 1 || parts[1].Length is >= 1 and <= 3)
            && parts.All(static part => part.All(static c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '$'));
    }

    /// <summary>
    /// The command tail of the program: the arguments joined by blanks, one with a blank put in
    /// double quotes, <c>%</c> doubled for the batch. Refused: characters outside printable ASCII,
    /// <c>|</c>, <c>&lt;</c>, <c>&gt;</c> and <c>"</c> (the batch would take them as its own), and a tail
    /// longer than DOS passes.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ProgramNotSupported"/>.</exception>
    public static string FormatArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var tail = new StringBuilder();
        foreach (string argument in arguments)
        {
            foreach (char c in argument)
            {
                if (c is < ' ' or > '~' or '|' or '<' or '>' or '"')
                {
                    throw new X86Exception(X86ErrorCodes.ProgramNotSupported, $"The argument '{argument}' has a character a DOS command line cannot carry (printable ASCII without | < > \" only).");
                }
            }

            if (tail.Length > 0)
            {
                tail.Append(' ');
            }

            tail.Append(argument.Length == 0 || argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument);
        }

        if (tail.Length > MaxArgumentsLength)
        {
            throw new X86Exception(X86ErrorCodes.ProgramNotSupported, $"The arguments are {tail.Length} characters; DOS passes at most {MaxArgumentsLength} to a program.");
        }

        return tail.ToString().Replace("%", "%%", StringComparison.Ordinal);
    }

    /// <summary>The text of <see cref="BatchFileName"/> (ASCII, CR LF) that runs <paramref name="shortName"/> from <c>D:</c>.</summary>
    public static string Batch(string shortName, string formattedArguments)
    {
        if (!IsShortName(shortName))
        {
            throw new ArgumentException($"'{shortName}' is not an 8.3 name.", nameof(shortName));
        }

        ArgumentNullException.ThrowIfNull(formattedArguments);
        string command = formattedArguments.Length == 0 ? $"D:\\{shortName}" : $"D:\\{shortName} {formattedArguments}";
        string[] lines =
        [
            "@ECHO OFF",
            "REM Os-x86 (work item 11): runs one DOS program, its end and ERRORLEVEL told on COM1.",
            "CLS",
            "D:",
            "CD \\",
            $"ECHO {StartMarker} >COM1",
            command,
            $"ECHO {ExitMarker} %ERRORLEVEL% >COM1",
            "PAUSE >NUL",
        ];
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Whether a line of COM1 is <see cref="StartMarker"/>.</summary>
    public static bool IsStart(string line) => line is not null && line.Trim() == StartMarker;

    /// <summary>The exit code of an <see cref="ExitMarker"/> line of COM1, or <see langword="null"/> for any other line.</summary>
    public static int? ParseExit(string line)
    {
        if (line is null)
        {
            return null;
        }

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(ExitMarker + " ", StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(trimmed.AsSpan(ExitMarker.Length + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code is >= 0 and <= 255 ? code : null;
    }
}
