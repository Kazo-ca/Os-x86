using System.Text;
using System.Threading.Channels;
using OsX86.Engine;
using OsX86.Interface;
using OsX86.Tests.Agent;
using OsX86.Vm;

namespace OsX86.Tests.Vm;

/// <summary>
/// An <see cref="IEngine"/> without emulator: it goes through the states, and its ports talk to a
/// <see cref="SimulatedAgentChannel"/> (the agent of work item 5's tests) on <c>virtio0</c> and
/// <c>virtio1</c>, or to nobody for an image without agent. The serial port is a byte queue the
/// test fills and reads. For the DOS way (work item 11) it is a text-mode engine: its screen is what
/// the test sets, the scancodes it receives are recorded, the serial lines are what the test sends.
/// </summary>
internal sealed class FakeEngine : IEngine, ITextModeEngine, INetworkEngine
{
    private readonly List<byte[]> _framesSent = [];

    private readonly Lock _lock = new();
    private readonly Channel<ReadOnlyMemory<byte>> _serial = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly Channel<string> _serialLines = Channel.CreateUnbounded<string>();
    private readonly List<byte> _scancodes = [];
    private EngineTextScreen _screen = new(80, 25, [.. Enumerable.Repeat(new string(' ', 80), 25)]);
    private readonly List<byte[]> _serialWritten = [];
    private EngineState _state;

    public FakeEngine(SimulatedAgentChannel? agent) => Agent = agent;

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<EngineErrorEventArgs>? ErrorReported;

    public event EventHandler<ScreenSizeChangedEventArgs>? ScreenSizeChanged;

    public event EventHandler<NetworkFramesEventArgs>? FramesReceived;

    /// <summary>The frames the host handed to the guest's card, in order.</summary>
    public IReadOnlyList<byte[]> FramesSent
    {
        get
        {
            lock (_lock)
            {
                return [.. _framesSent];
            }
        }
    }

    /// <summary>The guest's card sends frames.</summary>
    public void EmitFrames(params byte[][] frames) => FramesReceived?.Invoke(this, new NetworkFramesEventArgs(frames));

    public ValueTask SendFramesAsync(IReadOnlyList<byte[]> frames, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_state is not (EngineState.Running or EngineState.Paused))
            {
                throw new EngineStateException(EngineOperation.Write, _state, "not running");
            }

            _framesSent.AddRange(frames);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The agent of the guest, or <see langword="null"/> for an image without agent.</summary>
    public SimulatedAgentChannel? Agent { get; }

    /// <summary>When set, <see cref="StartAsync"/> fails with it after the move to Starting.</summary>
    public Exception? StartException { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> waits for it before reporting Running.</summary>
    public TaskCompletionSource? BootGate { get; set; }

    public EngineStartOptions? StartedWith { get; private set; }

    public int Stops { get; private set; }

    public bool Disposed { get; private set; }

    public EngineState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public IReadOnlyList<byte[]> SerialWritten
    {
        get
        {
            lock (_lock)
            {
                return [.. _serialWritten];
            }
        }
    }

    public async ValueTask StartAsync(EngineStartOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        StartedWith = options;
        Move(EngineState.Starting, null);
        if (BootGate is not null)
        {
            await BootGate.Task.WaitAsync(cancellationToken);
        }

        if (StartException is not null)
        {
            Move(EngineState.Stopped, "start-failed");
            throw StartException;
        }

        Move(EngineState.Running, null);
    }

    /// <summary>When set, <see cref="StopAsync"/> fails with it (not an engine error: a bug of the worker bridge).</summary>
    public Exception? StopException { get; set; }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (StopException is not null)
        {
            throw StopException;
        }

        if (State != EngineState.Stopped)
        {
            Stops++;
            Move(EngineState.Stopping, null);
            Move(EngineState.Stopped, "stopped");
        }

        Agent?.Complete();
        _serial.Writer.TryComplete();
        _serialLines.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>The guest dies by itself (a crash of the worker).</summary>
    public void Crash()
    {
        Move(EngineState.Stopped, "emulator-stopped");
        ErrorReported?.Invoke(this, new EngineErrorEventArgs(EngineErrorCodes.WorkerError, "crash", true));
    }

    /// <summary>The guest writes on its serial console.</summary>
    public void SendSerial(string text) => _serial.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    /// <summary>The guest writes a line on its serial console, as the line readers see it.</summary>
    public void SendSerialLine(string line) => _serialLines.Writer.TryWrite(line);

    /// <summary>The text screen the guest shows.</summary>
    public EngineTextScreen Screen
    {
        get
        {
            lock (_lock)
            {
                return _screen;
            }
        }

        set
        {
            lock (_lock)
            {
                _screen = value;
            }
        }
    }

    /// <summary>The scancodes received, in order.</summary>
    public IReadOnlyList<byte> Scancodes
    {
        get
        {
            lock (_lock)
            {
                return [.. _scancodes];
            }
        }
    }

    /// <summary>How many times the screen was read.</summary>
    public int ScreenReads { get; private set; }

    /// <summary>The guest changes its video mode.</summary>
    public void RaiseScreenSize(int columns, int rows, int bitsPerPixel) => ScreenSizeChanged?.Invoke(this, new ScreenSizeChangedEventArgs(columns, rows, bitsPerPixel));

    public ValueTask<EngineTextScreen> ReadTextScreenAsync(CancellationToken cancellationToken)
    {
        if (State != EngineState.Running)
        {
            throw new EngineStateException(EngineOperation.Write, State);
        }

        lock (_lock)
        {
            ScreenReads++;
            return ValueTask.FromResult(_screen);
        }
    }

    public ValueTask SendScancodesAsync(IReadOnlyList<byte> scancodes, CancellationToken cancellationToken)
    {
        if (State != EngineState.Running)
        {
            throw new EngineStateException(EngineOperation.Write, State);
        }

        if (StartedWith?.Keyboard != true)
        {
            throw new EngineException(EngineErrorCodes.InvalidState, "the keyboard of this guest is not enabled");
        }

        lock (_lock)
        {
            _scancodes.AddRange(scancodes);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public IEngineConsole GetConsole(ConsoleId id) => id switch
    {
        { Kind: ConsoleKind.Serial, Index: 0 } => new SerialPort(this, id),
        { Kind: ConsoleKind.Virtio, Index: 0 } => new ProtocolPort(this, id),
        { Kind: ConsoleKind.Virtio, Index: 1 } => new ConsolePort(this, id),
        _ => throw new ArgumentException($"No port {id}.", nameof(id)),
    };

    public ValueTask AttachDisplayAsync(object canvas, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DetachDisplayAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Agent?.Complete();
        _serial.Writer.TryComplete();
        _serialLines.Writer.TryComplete();
        _ = ScreenSizeChanged;
        return ValueTask.CompletedTask;
    }

    private void Move(EngineState state, string? reason)
    {
        EngineState previous;
        lock (_lock)
        {
            previous = _state;
            _state = state;
        }

        if (previous != state)
        {
            StateChanged?.Invoke(this, new EngineStateChangedEventArgs(previous, state, reason));
        }
    }

    private static async IAsyncEnumerable<T> Never<T>([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private sealed class SerialPort(FakeEngine engine, ConsoleId id) : IEngineConsole
    {
        public ConsoleId Id { get; } = id;

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(CancellationToken cancellationToken) => engine._serial.Reader.ReadAllAsync(cancellationToken);

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) => engine._serialLines.Reader.ReadAllAsync(cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (engine.State is EngineState.Stopped or EngineState.Stopping)
            {
                throw new EngineStateException(EngineOperation.Write, engine.State);
            }

            lock (engine._lock)
            {
                engine._serialWritten.Add(data.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask WriteLineAsync(string text, CancellationToken cancellationToken) => WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), cancellationToken);
    }

    private sealed class ProtocolPort(FakeEngine engine, ConsoleId id) : IEngineConsole
    {
        public ConsoleId Id { get; } = id;

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(CancellationToken cancellationToken) => Never<ReadOnlyMemory<byte>>(cancellationToken);

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) =>
            engine.Agent?.ReadLinesAsync(cancellationToken) ?? Never<string>(cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            WriteLineAsync(Encoding.UTF8.GetString(data.Span).TrimEnd('\n'), cancellationToken);

        public ValueTask WriteLineAsync(string text, CancellationToken cancellationToken) =>
            engine.Agent?.WriteLineAsync(text, cancellationToken) ?? ValueTask.CompletedTask;
    }

    private sealed class ConsolePort(FakeEngine engine, ConsoleId id) : IEngineConsole
    {
        public ConsoleId Id { get; } = id;

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(CancellationToken cancellationToken) =>
            engine.Agent?.ReadConsoleBytesAsync(cancellationToken) ?? Never<ReadOnlyMemory<byte>>(cancellationToken);

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) => Never<string>(cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            engine.Agent?.WriteConsoleBytesAsync(data, cancellationToken) ?? ValueTask.CompletedTask;

        public ValueTask WriteLineAsync(string text, CancellationToken cancellationToken) => WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), cancellationToken);
    }
}

/// <summary>Creates <see cref="FakeEngine"/>s: with a fresh simulated agent each run, or without agent.</summary>
internal sealed class FakeEngineFactory : IEngineFactory
{
    private readonly Lock _lock = new();
    private readonly List<FakeEngine> _created = [];

    /// <summary>Whether the guests have an agent.</summary>
    public bool WithAgent { get; set; } = true;

    /// <summary>Called on each new engine, to script its agent or its boot.</summary>
    public Action<FakeEngine>? Configure { get; set; }

    public IReadOnlyList<FakeEngine> Created
    {
        get
        {
            lock (_lock)
            {
                return [.. _created];
            }
        }
    }

    public FakeEngine Last => Created[^1];

    public IEngine Create()
    {
        var engine = new FakeEngine(WithAgent ? new SimulatedAgentChannel() : null);
        Configure?.Invoke(engine);
        lock (_lock)
        {
            _created.Add(engine);
        }

        return engine;
    }
}
