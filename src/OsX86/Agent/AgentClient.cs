using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OsX86.Agent;

/// <summary>The delays of an <see cref="AgentClient"/>.</summary>
public sealed record AgentClientOptions
{
    /// <summary>How long a reply may take before the request fails with <see cref="AgentTimeoutException"/> (the slowest ordinary operation, the process list, takes 0.4 to 0.7 s for 80 processes in v86, trial E7).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="AgentClient.ConnectAsync"/> waits for the agent to answer (a cold boot of the Linux image takes 45 to 70 s, trials E7 and E17).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How often <see cref="AgentClient.ConnectAsync"/> asks again while the agent is silent.</summary>
    public TimeSpan ConnectProbeInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>What is added to the duration an operation announces (a kill's grace period, a command's timeout) before the client gives up on its reply.</summary>
    public TimeSpan LongOperationMargin { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long after a <c>console-closed</c> event a console whose last frame never came is completed anyway.</summary>
    public TimeSpan ConsoleCloseFallback { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The host side of the guest agent protocol (decision 205 A): one JSON request per line with an
/// <c>id</c>, the reply paired by that <c>id</c> with a timeout, the events as a stream, and the
/// consoles demultiplexed from the console port. One method per operation (see the second part of
/// this class), strong types in and out, <see cref="AgentException"/> and its kinds for every
/// failure. Create it on a channel, call <see cref="ConnectAsync"/>, dispose it when the VM stops;
/// the agent keeps the launches when the client goes away.
/// </summary>
public sealed partial class AgentClient : IAsyncDisposable
{
    /// <summary>Events kept for a reader that does not keep up; the oldest are dropped beyond.</summary>
    public const int EventQueueCapacity = 1024;

    private const int MaxOrphanConsoles = 32;
    private const int MaxOrphanBytesPerConsole = 256 * 1024;

    private readonly IAgentChannel _channel;
    private readonly AgentClientOptions _options;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _consoleWrite = new(1, 1);
    private readonly Dictionary<long, PendingRequest> _pending = [];
    private readonly Dictionary<LaunchId, TaskCompletionSource<ProcessExit>> _launches = [];
    private readonly Dictionary<int, ConsoleHandle> _consoles = [];
    private readonly SortedDictionary<int, OrphanFrames> _orphans = [];
    private readonly List<Channel<AgentEvent>> _eventReaders = [];
    private Task? _lineLoop;
    private Task? _consoleLoop;
    private long _nextId;
    private bool _closed;
    private AgentStatus? _status;

    /// <summary>Creates a client on a channel; nothing is read or written before <see cref="ConnectAsync"/>.</summary>
    public AgentClient(IAgentChannel channel, AgentClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _options = options ?? new AgentClientOptions();
    }

    /// <summary>Whether the agent answered <see cref="ConnectAsync"/> and the client is not closed.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _status is not null && !_closed;
            }
        }
    }

    /// <summary>What the agent said of itself when it answered <see cref="ConnectAsync"/>; <see langword="null"/> before.</summary>
    public AgentStatus? Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Starts reading both ports and waits until the agent answers: a <c>ping</c> is sent every
    /// <see cref="AgentClientOptions.ConnectProbeInterval"/> until a reply comes (the agent's own
    /// <c>ready</c> event may have been written before anyone listened). Call it once, as soon as
    /// the engine starts.
    /// </summary>
    /// <exception cref="AgentTimeoutException">No reply within <see cref="AgentClientOptions.ConnectTimeout"/> (<see cref="AgentErrorCodes.NotReady"/>): the image has no agent, or the guest did not boot.</exception>
    /// <exception cref="AgentProtocolException">The agent speaks another version of the protocol.</exception>
    public async Task<AgentStatus> ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ThrowIfClosedUnsafe();
            if (_lineLoop is not null)
            {
                throw new InvalidOperationException("ConnectAsync was already called on this client.");
            }

            // The readers are registered here, before the first request leaves.
            _lineLoop = ReadLinesAsync(_channel.ReadLinesAsync(_stop.Token));
            _consoleLoop = ReadConsoleAsync(_channel.ReadConsoleBytesAsync(_stop.Token));
        }

        long deadline = Environment.TickCount64 + (long)_options.ConnectTimeout.TotalMilliseconds;
        while (true)
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                throw new AgentTimeoutException(AgentErrorCodes.NotReady, "connect", _options.ConnectTimeout);
            }

            TimeSpan probe = TimeSpan.FromMilliseconds(Math.Min(remaining, Math.Max(1, _options.ConnectProbeInterval.TotalMilliseconds)));
            try
            {
                AgentMessage reply = await SendAsync(new AgentRequest { Op = AgentProtocol.Operations.Ping }, probe, null, cancellationToken).ConfigureAwait(false);
                return Accept(AgentProtocol.ToStatus(reply));
            }
            catch (AgentTimeoutException)
            {
                // Not listening yet: ask again until the connection deadline.
            }
        }
    }

    /// <summary>
    /// The events of the agent from the call on. Each call is an independent reader with a bounded
    /// queue (<see cref="EventQueueCapacity"/>); it ends with its token or when the client closes.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken)
    {
        Channel<AgentEvent> channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(EventQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        lock (_lock)
        {
            if (_closed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _eventReaders.Add(channel);
            }
        }

        return EnumerateEventsAsync(channel, cancellationToken);
    }

    /// <summary>
    /// Stops reading, fails every pending request, launch and console with
    /// <see cref="AgentClosedException"/> and ends the event readers. Nothing is sent to the agent:
    /// what runs in the guest keeps running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Close("The agent client was disposed.");
        Task?[] loops;
        lock (_lock)
        {
            loops = [_lineLoop, _consoleLoop];
        }

        foreach (Task? loop in loops)
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
        }

        _stop.Dispose();
        _consoleWrite.Dispose();
    }

    private void ThrowIfClosedUnsafe()
    {
        if (_closed)
        {
            throw new AgentClosedException("The agent client is closed.");
        }
    }

    private AgentStatus Accept(AgentStatus status)
    {
        if (status.ProtocolVersion != AgentProtocol.Version)
        {
            throw new AgentProtocolException(AgentErrorCodes.UnsupportedVersion,
                $"The agent speaks version {status.ProtocolVersion} of the protocol; this client speaks version {AgentProtocol.Version}.");
        }

        lock (_lock)
        {
            _status = status;
        }

        return status;
    }

    /// <summary>
    /// Sends a request and waits for its reply. <paramref name="onReply"/> runs in the reader, before
    /// any later line is looked at: that is where a launch or a console is registered, so that its
    /// exit, which may be the very next line, finds it.
    /// </summary>
    private async Task<AgentMessage> SendAsync(AgentRequest request, TimeSpan timeout, Action<AgentMessage>? onReply, CancellationToken cancellationToken)
    {
        var pending = new PendingRequest(request.Op, onReply);
        lock (_lock)
        {
            ThrowIfClosedUnsafe();
            if (_lineLoop is null)
            {
                throw new InvalidOperationException("Call ConnectAsync before any operation.");
            }

            request.Id = ++_nextId;
            _pending.Add(request.Id, pending);
        }

        try
        {
            await _channel.WriteLineAsync(AgentProtocol.Serialize(request), cancellationToken).ConfigureAwait(false);
            AgentMessage reply = await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return reply.Ok == true ? reply : throw AgentProtocol.ToException(reply);
        }
        catch (TimeoutException)
        {
            throw new AgentTimeoutException(AgentErrorCodes.Timeout, request.Op, timeout);
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(request.Id);
            }
        }
    }

    private async Task ReadLinesAsync(IAsyncEnumerable<string> lines)
    {
        try
        {
            await foreach (string line in lines.ConfigureAwait(false))
            {
                if (line.Length > 0)
                {
                    HandleLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        finally
        {
            Close("The agent channel ended.");
        }
    }

    private void HandleLine(string line)
    {
        try
        {
            AgentMessage message = AgentProtocol.Parse(line);
            if (message.Event is not null)
            {
                HandleEvent(AgentProtocol.ToEvent(message));
            }
            else
            {
                HandleReply(message);
            }
        }
        catch (AgentException exception)
        {
            Publish(new AgentFaultEvent(exception.Code, exception.Message));
        }
    }

    private void HandleReply(AgentMessage reply)
    {
        if (reply.Id is not { } id)
        {
            // An error the agent could not attach to a request (a line it could not parse).
            Publish(new AgentFaultEvent(reply.Error ?? AgentErrorCodes.Protocol, reply.Message ?? "The agent sent a reply without id."));
            return;
        }

        PendingRequest? pending;
        lock (_lock)
        {
            _pending.Remove(id, out pending);
        }

        if (pending is null)
        {
            return; // a reply that outlived its timeout
        }

        if (reply.Ok == true && pending.OnReply is not null)
        {
            try
            {
                pending.OnReply(reply);
            }
            catch (AgentException exception)
            {
                pending.Completion.TrySetException(exception);
                return;
            }
        }

        pending.Completion.TrySetResult(reply);
    }

    private void HandleEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case LaunchExitedEvent exited:
                TaskCompletionSource<ProcessExit>? launch;
                lock (_lock)
                {
                    _launches.Remove(exited.Launch, out launch);
                }

                launch?.TrySetResult(exited.Exit);
                break;
            case ConsoleClosedEvent closed:
                _ = CompleteConsoleLaterAsync(closed);
                break;
        }

        Publish(agentEvent);
    }

    /// <summary>
    /// The exit of a console normally comes with its last frame on the console port; if that frame
    /// never arrives, the event of the protocol port completes the console after a short wait.
    /// </summary>
    private async Task CompleteConsoleLaterAsync(ConsoleClosedEvent closed)
    {
        try
        {
            await Task.Delay(_options.ConsoleCloseFallback, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ConsoleHandle? console;
        lock (_lock)
        {
            _consoles.Remove(closed.Console, out console);
        }

        console?.Complete(closed.Exit);
    }

    private async Task ReadConsoleAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        var reader = new ConsoleFrameReader();
        try
        {
            await foreach (ReadOnlyMemory<byte> chunk in chunks.ConfigureAwait(false))
            {
                foreach (ConsoleFrame frame in reader.Push(chunk.Span))
                {
                    HandleFrame(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private void HandleFrame(ConsoleFrame frame)
    {
        if (frame.Type is not (ConsoleFrameType.Data or ConsoleFrameType.Closed))
        {
            return; // a frame type of a newer agent
        }

        // Everything under the lock: frames of one console must reach it in order, including the
        // ones that arrived before the reply that registers the console.
        lock (_lock)
        {
            if (_consoles.TryGetValue(frame.Console, out ConsoleHandle? console))
            {
                DeliverUnsafe(console, frame);
            }
            else if (!_closed)
            {
                KeepOrphanUnsafe(frame);
            }
        }
    }

    private void DeliverUnsafe(ConsoleHandle console, ConsoleFrame frame)
    {
        if (frame.Type == ConsoleFrameType.Data)
        {
            console.Deliver(frame.Payload);
        }
        else
        {
            _consoles.Remove(frame.Console);
            console.Complete(frame.TryReadExit(out ProcessExit exit) ? exit : new ProcessExit(-1, 0));
        }
    }

    private void KeepOrphanUnsafe(ConsoleFrame frame)
    {
        if (!_orphans.TryGetValue(frame.Console, out OrphanFrames? orphan))
        {
            if (_orphans.Count >= MaxOrphanConsoles)
            {
                _orphans.Remove(_orphans.Keys.First());
            }

            orphan = new OrphanFrames();
            _orphans.Add(frame.Console, orphan);
        }

        if (orphan.Bytes + frame.Payload.Length <= MaxOrphanBytesPerConsole)
        {
            orphan.Frames.Add(frame);
            orphan.Bytes += frame.Payload.Length;
        }
    }

    /// <summary>Registers a console (called in the reader, on the reply to <c>open-console</c>) and gives it the frames that came first.</summary>
    private void RegisterConsole(ConsoleHandle console)
    {
        lock (_lock)
        {
            _consoles.Add(console.Number, console);
            if (_orphans.Remove(console.Number, out OrphanFrames? orphan))
            {
                foreach (ConsoleFrame frame in orphan.Frames)
                {
                    DeliverUnsafe(console, frame);
                }
            }
        }
    }

    private Task<ProcessExit> RegisterLaunch(LaunchId id)
    {
        var exited = new TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _launches[id] = exited;
        }

        return exited.Task;
    }

    private void Publish(AgentEvent agentEvent)
    {
        lock (_lock)
        {
            foreach (Channel<AgentEvent> reader in _eventReaders)
            {
                reader.Writer.TryWrite(agentEvent);
            }
        }
    }

    private async IAsyncEnumerable<AgentEvent> EnumerateEventsAsync(Channel<AgentEvent> channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AgentEvent agentEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return agentEvent;
            }
        }
        finally
        {
            lock (_lock)
            {
                _eventReaders.Remove(channel);
            }
        }
    }

    private void Close(string reason)
    {
        PendingRequest[] pending;
        TaskCompletionSource<ProcessExit>[] launches;
        ConsoleHandle[] consoles;
        Channel<AgentEvent>[] readers;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = [.. _pending.Values];
            launches = [.. _launches.Values];
            consoles = [.. _consoles.Values];
            readers = [.. _eventReaders];
            _pending.Clear();
            _launches.Clear();
            _consoles.Clear();
            _orphans.Clear();
            _eventReaders.Clear();
        }

        _stop.Cancel();
        var closed = new AgentClosedException(reason);
        foreach (PendingRequest request in pending)
        {
            request.Completion.TrySetException(closed);
        }

        foreach (TaskCompletionSource<ProcessExit> launch in launches)
        {
            launch.TrySetException(closed);
        }

        foreach (ConsoleHandle console in consoles)
        {
            console.Fail(closed);
        }

        foreach (Channel<AgentEvent> reader in readers)
        {
            reader.Writer.TryComplete();
        }
    }

    private sealed class PendingRequest(string operation, Action<AgentMessage>? onReply)
    {
        public string Operation { get; } = operation;

        public Action<AgentMessage>? OnReply { get; } = onReply;

        public TaskCompletionSource<AgentMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class OrphanFrames
    {
        public List<ConsoleFrame> Frames { get; } = [];

        public int Bytes { get; set; }
    }
}
