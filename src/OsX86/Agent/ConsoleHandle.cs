using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace OsX86.Agent;

/// <summary>What closing a console did (decision 208 A).</summary>
/// <param name="KeptProcesses">How many processes were moved out of the console before it was closed.</param>
/// <param name="Kept">The launch that now holds them; <see langword="null"/> when nothing was kept. Its end comes as a <see cref="LaunchExitedEvent"/>.</param>
public sealed record ConsoleCloseResult(int KeptProcesses, LaunchId? Kept);

/// <summary>
/// An open console of the guest (decision 213 A): a pseudo-terminal whose program runs as the
/// prefix user. The bytes the program writes come out of <see cref="ReadOutputAsync"/>; the bytes
/// written here are its keyboard (Ctrl+C is the byte 0x03, which the terminal turns into SIGINT);
/// <see cref="Exited"/> gives the exit code, which arrives on the same channel after the last
/// byte of output. Obtained from <see cref="AgentClient.OpenConsoleAsync"/>.
/// </summary>
public sealed class ConsoleHandle
{
    /// <summary>Chunks of output kept for a reader that does not keep up; the oldest are dropped beyond.</summary>
    public const int OutputQueueCapacity = 4096;

    private static readonly byte[] InterruptByte = [0x03];

    private readonly AgentClient _client;
    private readonly Channel<ReadOnlyMemory<byte>> _output = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(OutputQueueCapacity)
    {
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ConsoleHandle(AgentClient client, LaunchId launch, int number, int processId, int columns, int rows)
    {
        _client = client;
        Launch = launch;
        Number = number;
        ProcessId = processId;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>The launch of the console: its program and everything started from it share that cgroup.</summary>
    public LaunchId Launch { get; }

    /// <summary>The console number, which tags its frames on the console port.</summary>
    public int Number { get; }

    /// <summary>The pid of the program on the terminal; informational.</summary>
    public int ProcessId { get; }

    /// <summary>The width the terminal was opened with, or last resized to.</summary>
    public int Columns { get; private set; }

    /// <summary>The height the terminal was opened with, or last resized to.</summary>
    public int Rows { get; private set; }

    /// <summary>
    /// Completes with the exit code or signal of the program once the console is over and all its
    /// output has been delivered; fails with <see cref="AgentClosedException"/> when the client closes first.
    /// </summary>
    public Task<ProcessExit> Exited => _exited.Task;

    /// <summary>Whether the console is over.</summary>
    public bool IsClosed => _exited.Task.IsCompleted;

    /// <summary>
    /// The bytes the console writes, in order, from the opening on; ends when the console is over.
    /// There is one output stream: several readers share it, each chunk going to one of them.
    /// </summary>
    /// <param name="cancellationToken">Stops this reader; the console stays open.</param>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ReadOnlyMemory<byte> chunk in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>Sends bytes to the terminal, as typed.</summary>
    /// <exception cref="AgentException">The console is over (<see cref="AgentErrorCodes.UnknownConsole"/>).</exception>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        return data.IsEmpty ? ValueTask.CompletedTask : _client.WriteConsoleAsync(Number, data, cancellationToken);
    }

    /// <summary>Sends text to the terminal, encoded in UTF-8, as typed (no line end is added).</summary>
    /// <exception cref="AgentException">The console is over (<see cref="AgentErrorCodes.UnknownConsole"/>).</exception>
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>Sends Ctrl+C: the byte 0x03, which the terminal turns into SIGINT for the foreground program (decision 216 A).</summary>
    public ValueTask SendInterruptAsync(CancellationToken cancellationToken) => WriteAsync(InterruptByte, cancellationToken);

    /// <summary>Changes the size of the terminal; the foreground program gets SIGWINCH.</summary>
    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        await _client.ResizeConsoleAsync(Number, columns, rows, cancellationToken).ConfigureAwait(false);
        Columns = columns;
        Rows = rows;
    }

    /// <summary>
    /// Closes the console (decision 208 A): the processes named in <paramref name="keepProcessIds"/>
    /// (those that have a window) move to a launch of their own and live on, the rest gets SIGHUP
    /// and, after <paramref name="grace"/> (2 s when <see langword="null"/>), is killed. Returns as soon
    /// as the agent has started; <see cref="Exited"/> completes when the console is over.
    /// </summary>
    public Task<ConsoleCloseResult> CloseAsync(TimeSpan? grace = null, IReadOnlyList<int>? keepProcessIds = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        return _client.CloseConsoleAsync(Number, grace, keepProcessIds, cancellationToken);
    }

    internal void Deliver(ReadOnlyMemory<byte> bytes) => _output.Writer.TryWrite(bytes);

    internal void Complete(ProcessExit exit)
    {
        _output.Writer.TryComplete();
        _exited.TrySetResult(exit);
    }

    internal void Fail(Exception exception)
    {
        _output.Writer.TryComplete();
        _exited.TrySetException(exception);
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new AgentException(AgentErrorCodes.UnknownConsole, $"Console {Number} ({Launch}) is closed.");
        }
    }
}
