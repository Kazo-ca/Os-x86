using System.Runtime.CompilerServices;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Interface;

namespace OsX86.Vm;

/// <summary>What the two console kinds share: the application they hold, closed once.</summary>
internal abstract class VmConsoleBase : IVmConsole
{
    private readonly VmApplication _application;
    private int _disposed;

    protected VmConsoleBase(Guid vmId, VmApplication application)
    {
        VmId = vmId;
        _application = application;
    }

    /// <inheritdoc />
    public Guid VmId { get; }

    /// <inheritdoc />
    public abstract bool IsSerial { get; }

    /// <inheritdoc />
    public abstract Task<int> Exited { get; }

    /// <summary>Whether <see cref="DisposeAsync"/> ran.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public abstract IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <inheritdoc />
    public ValueTask SendInterruptAsync(CancellationToken cancellationToken) => WriteAsync(new byte[] { 0x03 }, cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _application.Dispose();
        }
    }

    /// <summary>Ends the program of the console, when it still runs.</summary>
    protected abstract ValueTask CloseAsync();

    /// <summary>The exit code a shell reports for a process end.</summary>
    internal static int ToExitCode(ProcessExit exit) => exit.Signaled ? 128 + exit.Signal : exit.Code;

    /// <summary>Turns the exceptions of the agent client into <see cref="X86Exception"/>.</summary>
    internal static X86Exception Translate(Exception exception) => exception switch
    {
        X86Exception x86 => x86,
        AgentClosedException closed => new X86Exception(X86ErrorCodes.ConsoleClosed, closed.Message, closed),
        AgentException agent => new X86Exception(X86ErrorCodes.AgentError, $"{agent.Code}: {agent.Message}", agent),
        EngineStateException state => new X86Exception(X86ErrorCodes.ConsoleClosed, state.Message, state),
        EngineException engine => new X86Exception(X86ErrorCodes.ConsoleClosed, $"{engine.Code}: {engine.Message}", engine),
        _ => new X86Exception(X86ErrorCodes.ConsoleClosed, exception.Message, exception),
    };
}

/// <summary>A console of the guest agent (213 A): a pseudo-terminal, Ctrl+C by the line discipline, the exit code after the last bytes.</summary>
internal sealed class AgentVmConsole : VmConsoleBase
{
    private readonly ConsoleHandle _handle;
    private readonly TimeSpan _closeGrace;

    public AgentVmConsole(Guid vmId, VmApplication application, ConsoleHandle handle, TimeSpan closeGrace)
        : base(vmId, application)
    {
        _handle = handle;
        _closeGrace = closeGrace;
        Exited = TranslateAsync(handle.Exited);
    }

    /// <inheritdoc />
    public override bool IsSerial => false;

    /// <inheritdoc />
    public override Task<int> Exited { get; }

    /// <summary>The launch of the console in the guest.</summary>
    public LaunchId Launch => _handle.Launch;

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator = _handle.ReadOutputAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Translate(exception); // the agent closed or the VM stopped mid-read: a contract error, not an internal one
            }

            yield return enumerator.Current;
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _handle.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public override async ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        try
        {
            await _handle.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask CloseAsync()
    {
        if (_handle.IsClosed)
        {
            return;
        }

        try
        {
            await _handle.CloseAsync(_closeGrace, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentException)
        {
            // Already gone (unknown-console), or the client closed under it: nothing left to end.
        }
    }

    private static async Task<int> TranslateAsync(Task<ProcessExit> exited)
    {
        try
        {
            return ToExitCode(await exited.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            throw Translate(exception);
        }
    }
}

/// <summary>
/// The serial console of an image without agent (212 A): the whole <c>ttyS0</c> of the guest, shared
/// with the boot messages; no exit code (0 when closed); Ctrl+C is the byte 0x03 the guest's line
/// discipline turns into SIGINT, like the agent's ptys.
/// </summary>
internal sealed class SerialVmConsole : VmConsoleBase
{
    private readonly IEngineConsole _port;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new(); // ends the readers when the console closes; never disposed (its token outlives readers)

    public SerialVmConsole(Guid vmId, VmApplication application, IEngineConsole port)
        : base(vmId, application)
    {
        _port = port;
    }

    /// <inheritdoc />
    public override bool IsSerial => true;

    /// <inheritdoc />
    public override Task<int> Exited => _exited.Task;

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        IAsyncEnumerable<ReadOnlyMemory<byte>> bytes = _port.ReadRawAsync(linked.Token);
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator = bytes.GetAsyncEnumerator(linked.Token);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                yield break; // the console was closed, not the reader
            }

            yield return enumerator.Current;
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_exited.Task.IsCompleted)
        {
            throw new X86Exception(X86ErrorCodes.ConsoleClosed, "The serial console is closed.");
        }

        try
        {
            await _port.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public override ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>The VM stopped under the console.</summary>
    internal void Fail(X86Exception exception)
    {
        if (_exited.TrySetException(exception))
        {
            _ = _exited.Task.Exception; // observed here: Exited reports it to whoever awaits it
            _closed.Cancel();
        }
    }

    /// <inheritdoc />
    protected override ValueTask CloseAsync()
    {
        if (_exited.TrySetResult(0))
        {
            _closed.Cancel();
        }

        return ValueTask.CompletedTask;
    }
}
