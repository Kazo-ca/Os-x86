namespace OsX86.Interface;

/// <summary>
/// A program to run in a console (<see cref="Ix86.RunProgramAsync"/>, work item 10): the bytes of
/// the program file, its name, its arguments, and where it runs. Os-x86 detects its type (162 A),
/// chooses a compatible VM and the prefix (193 A) when they are not given, copies the file into the
/// prefix and starts it on a pseudo-terminal (213 A).
/// </summary>
public sealed record ProgramRunOptions
{
    /// <summary>The largest program accepted, in bytes (copied through the agent, about 36 KiB per request).</summary>
    public const int MaxContentBytes = 256 * 1024 * 1024;

    /// <summary>The file name of the program, with its extension (<c>hello.exe</c>); a path is reduced to its last part.</summary>
    public required string FileName { get; init; }

    /// <summary>The bytes of the program file.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The arguments after the program name, passed as they are (never through a shell).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The VM, or <see langword="null"/> to let Os-x86 choose one of the caller's compatible VMs (and make one when there is none).</summary>
    public Guid? VmId { get; init; }

    /// <summary>The prefix (decision 200 B), or <see langword="null"/> for the VM's default prefix (decision 193 A).</summary>
    public string? Prefix { get; init; }

    /// <summary>The width of the terminal in character cells (1 to 1000).</summary>
    public int Columns { get; init; } = 80;

    /// <summary>The height of the terminal in character cells (1 to 1000).</summary>
    public int Rows { get; init; } = 25;
}

/// <summary>How a console is opened (<see cref="Ix86.OpenConsoleAsync"/>).</summary>
public sealed record VmConsoleOptions
{
    /// <summary>
    /// The prefix the shell runs for (decision 200 B: the guest user of the prefix), or
    /// <see langword="null"/> for the default prefix of the VM's profile. Ignored by a serial console.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>The width of the terminal in character cells (1 to 1000).</summary>
    public int Columns { get; init; } = 80;

    /// <summary>The height of the terminal in character cells (1 to 1000).</summary>
    public int Rows { get; init; } = 25;
}

/// <summary>
/// A console open in a VM (decision 213 A): bytes both ways, Ctrl+C to the guest program (216 A),
/// the exit code at the end (110). Disposing it closes it (208 A: what the console started dies with
/// it) and releases the VM, which stops with its last application (166 A).
/// </summary>
public interface IVmConsole : IAsyncDisposable
{
    /// <summary>The VM the console is open in.</summary>
    Guid VmId { get; }

    /// <summary>
    /// Whether this is the serial console of an image without agent (decision 212 A) rather than a
    /// pseudo-terminal of the agent: no exit code then (<see cref="Exited"/> gives 0 when closed), and
    /// the whole guest console is shared with the boot messages.
    /// </summary>
    bool IsSerial { get; }

    /// <summary>What the guest writes, as it arrives.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken cancellationToken);

    /// <summary>Sends keystrokes to the guest.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ConsoleClosed"/>.</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Sends Ctrl+C to the guest program (decision 216 A).</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ConsoleClosed"/>.</exception>
    ValueTask SendInterruptAsync(CancellationToken cancellationToken);

    /// <summary>Tells the guest the terminal's new size; a no-op on a serial console.</summary>
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);

    /// <summary>
    /// Completes when the program of the console ends, with its exit code (128 plus the signal number
    /// when a signal ended it, as a shell reports it). Fails when the VM stops under it.
    /// </summary>
    Task<int> Exited { get; }
}
