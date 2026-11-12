namespace OsX86.Engine.V86;

/// <summary>
/// The v86 engine: <see cref="IEngine"/> over the Web Worker of <c>wwwroot/engine/v86-worker.js</c>,
/// reached through an <see cref="IEngineWorkerChannel"/>. Keeps the state the worker reports,
/// turns its errors into exceptions of the pending operation, and routes the console bytes.
/// One operation (start, stop, pause, resume) is pending at a time; a stop may pre-empt the
/// others. Events are raised after the pending operation has been completed, outside the lock.
/// </summary>
public sealed class V86Engine : IEngine
{
    private readonly IEngineWorkerChannel _channel;
    private readonly Lock _lock = new();
    private readonly Dictionary<ConsoleId, V86Console> _consoles = [];
    private PendingOperation? _pending;
    private Task? _channelStart;
    private bool _disposed;
    private bool _workerReady;
    private EngineErrorEventArgs? _lastError;
    private EngineState _state;

    /// <summary>Creates an engine on a worker channel; the worker starts with the first operation.</summary>
    public V86Engine(IEngineWorkerChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    /// <inheritdoc />
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

    /// <summary>Whether the worker reported that v86 is loaded.</summary>
    public bool WorkerReady
    {
        get
        {
            lock (_lock)
            {
                return _workerReady;
            }
        }
    }

    /// <summary>The last error the worker reported, if any (cleared by a start).</summary>
    public EngineErrorEventArgs? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<EngineErrorEventArgs>? ErrorReported;

    /// <inheritdoc />
    public event EventHandler<ScreenSizeChangedEventArgs>? ScreenSizeChanged;

    /// <inheritdoc />
    public async ValueTask StartAsync(EngineStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await RunOperationAsync(EngineOperation.Start, WorkerProtocol.Start(options), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken) =>
        RunOperationAsync(EngineOperation.Stop, WorkerProtocol.Stop(), cancellationToken);

    /// <inheritdoc />
    public ValueTask PauseAsync(CancellationToken cancellationToken) =>
        RunOperationAsync(EngineOperation.Pause, WorkerProtocol.Pause(), cancellationToken);

    /// <inheritdoc />
    public ValueTask ResumeAsync(CancellationToken cancellationToken) =>
        RunOperationAsync(EngineOperation.Resume, WorkerProtocol.Resume(), cancellationToken);

    /// <inheritdoc />
    public IEngineConsole GetConsole(ConsoleId id)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException($"The engine has no console port {id.Kind} {id.Index}.", nameof(id));
        }

        lock (_lock)
        {
            if (!_consoles.TryGetValue(id, out V86Console? console))
            {
                console = new V86Console(id, WriteToGuestAsync);
                _consoles.Add(id, console);
            }

            return console;
        }
    }

    /// <inheritdoc />
    public async ValueTask AttachDisplayAsync(object canvas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        await EnsureChannelAsync(cancellationToken).ConfigureAwait(false);
        await _channel.AttachCanvasAsync(canvas, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DetachDisplayAsync(CancellationToken cancellationToken)
    {
        await EnsureChannelAsync(cancellationToken).ConfigureAwait(false);
        await _channel.DetachCanvasAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the engine: the worker is terminated (no clean stop: call <see cref="StopAsync"/> first
    /// when the guest matters), the pending operation fails with <see cref="EngineErrorCodes.Disposed"/>,
    /// every console reader completes, and a last <see cref="StateChanged"/> reports <see cref="EngineState.Stopped"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? channelStart;
        PendingOperation? pending;
        EngineStateChangedEventArgs? change;
        V86Console[] consoles;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            channelStart = _channelStart;
            pending = _pending;
            _pending = null;
            SetStateUnsafe(EngineState.Stopped, "disposed", out change);
            consoles = [.. _consoles.Values];
        }

        pending?.Completion.TrySetException(new EngineException(EngineErrorCodes.Disposed, "The engine was disposed."));
        foreach (V86Console console in consoles)
        {
            console.Complete();
        }

        if (channelStart is not null)
        {
            try
            {
                await channelStart.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The channel never started: nothing to dispose on its side.
                channelStart = null;
            }

            if (channelStart is not null)
            {
                await _channel.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
    }

    /// <summary>Handles a message posted by the worker (the callback given to the channel).</summary>
    internal ValueTask OnWorkerMessageAsync(string json)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
        }

        WorkerMessage message;
        try
        {
            message = WorkerProtocol.Parse(json);
        }
        catch (EngineException exception)
        {
            ReportError(exception.Code, exception.Message, fatal: false, fromWorker: false);
            return ValueTask.CompletedTask;
        }

        switch (message.Type)
        {
            case WorkerProtocol.Types.Ready:
                lock (_lock)
                {
                    _workerReady = true;
                }

                break;
            case WorkerProtocol.Types.State:
                if (EngineStateMachine.TryParse(message.State, out EngineState state))
                {
                    ReportState(state, message.Reason);
                }
                else
                {
                    ReportError(EngineErrorCodes.Protocol, $"The worker reported an unknown state '{message.State}'.", fatal: false, fromWorker: false);
                }

                break;
            case WorkerProtocol.Types.Error:
                ReportError(message.Code ?? EngineErrorCodes.WorkerError, message.Message ?? "unknown error", message.Fatal, fromWorker: true);
                break;
            case WorkerProtocol.Types.Console:
                DeliverConsole(message);
                break;
            case WorkerProtocol.Types.ScreenSize:
                if (message.Cols > 0 && message.Rows > 0 && message.Bpp >= 0)
                {
                    ScreenSizeChanged?.Invoke(this, new ScreenSizeChangedEventArgs(message.Cols, message.Rows, message.Bpp));
                }
                else
                {
                    ReportError(EngineErrorCodes.Protocol, $"The worker reported an impossible screen size {message.Cols}x{message.Rows}x{message.Bpp}.", fatal: false, fromWorker: false);
                }

                break;
            case WorkerProtocol.Types.Event:
                break;
            default:
                ReportError(EngineErrorCodes.Protocol, $"The worker sent an unknown message type '{message.Type}'.", fatal: false, fromWorker: false);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask RunOperationAsync(EngineOperation operation, string command, CancellationToken cancellationToken)
    {
        if (operation == EngineOperation.Stop)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state == EngineState.Stopped)
                {
                    return; // nothing to stop, and no reason to start the worker for it
                }
            }
        }

        await EnsureChannelAsync(cancellationToken).ConfigureAwait(false);
        (PendingOperation? pending, bool post, EngineState previous, EngineStateChangedEventArgs? change, PendingOperation? preempted) = BeginOperation(operation);
        preempted?.Completion.TrySetException(new EngineException(EngineErrorCodes.Stopped, $"The {Name(preempted.Operation)} was abandoned by a stop."));
        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }

        if (pending is null)
        {
            return; // nothing to do: stopping an engine that is already stopped
        }

        bool posted = false;
        try
        {
            if (post)
            {
                await _channel.PostAsync(command, cancellationToken).ConfigureAwait(false);
            }

            posted = true;
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled, or the channel could not post: the operation is abandoned here. When the
            // command never reached the worker, the engine is still what it was; otherwise the
            // worker goes on and its next reports bring the state up to date.
            EngineStateChangedEventArgs? rollback = null;
            lock (_lock)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    if (!posted)
                    {
                        SetStateUnsafe(previous, "not-sent", out rollback);
                    }
                }
            }

            if (rollback is not null)
            {
                StateChanged?.Invoke(this, rollback);
            }

            throw;
        }
    }

    /// <summary>
    /// Checks and registers an operation under the lock. Returns the pending operation to await
    /// (<see langword="null"/> for a no-op), whether a command must be posted (a stop that joins a
    /// stop already under way posts nothing), the state before the request, the transient change
    /// to raise, and the operation a stop pre-empted.
    /// </summary>
    private (PendingOperation? Pending, bool Post, EngineState Previous, EngineStateChangedEventArgs? Change, PendingOperation? Preempted) BeginOperation(EngineOperation operation)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EngineState previous = _state;
            if (operation == EngineOperation.Stop)
            {
                if (_state == EngineState.Stopped)
                {
                    return (null, false, previous, null, null);
                }

                if (_pending is { Operation: EngineOperation.Stop } stopping)
                {
                    return (stopping, false, previous, null, null);
                }

                if (_state == EngineState.Stopping)
                {
                    // The worker is stopping on its own: wait for it without asking again.
                    _pending = new PendingOperation(EngineOperation.Stop, EngineState.Stopped);
                    return (_pending, false, previous, null, null);
                }
            }

            if (!EngineStateMachine.CanRequest(_state, operation))
            {
                throw new EngineStateException(operation, _state);
            }

            PendingOperation? preempted = null;
            if (_pending is not null)
            {
                if (operation != EngineOperation.Stop)
                {
                    throw new EngineStateException(operation, _state, $"Cannot {Name(operation)} the engine: a {Name(_pending.Operation)} is in progress.");
                }

                preempted = _pending;
            }

            if (operation == EngineOperation.Start)
            {
                _lastError = null;
            }

            _pending = new PendingOperation(operation, EngineStateMachine.TargetState(operation));
            EngineStateChangedEventArgs? change = null;
            EngineState transient = EngineStateMachine.TransientState(operation);
            if (transient != _state)
            {
                SetStateUnsafe(transient, null, out change);
            }

            return (_pending, true, previous, change, preempted);
        }
    }

    private async Task EnsureChannelAsync(CancellationToken cancellationToken)
    {
        Task start;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _channelStart ??= _channel.StartAsync(OnWorkerMessageAsync, CancellationToken.None).AsTask();
            start = _channelStart;
        }

        try
        {
            await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (start.IsFaulted)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_channelStart, start))
                {
                    _channelStart = null; // the next operation tries again
                }
            }

            throw;
        }
    }

    private async ValueTask WriteToGuestAsync(ConsoleId console, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!EngineStateMachine.CanRequest(_state, EngineOperation.Write))
            {
                throw new EngineStateException(EngineOperation.Write, _state, $"Cannot write to {console}: the engine is {Name(_state)}.");
            }
        }

        await _channel.PostAsync(WorkerProtocol.Write(console, data.Span), cancellationToken).ConfigureAwait(false);
    }

    private void DeliverConsole(WorkerMessage message)
    {
        if (!ConsoleId.TryParse(message.Channel, out ConsoleId id))
        {
            ReportError(EngineErrorCodes.Protocol, $"The worker sent console data for an unknown channel '{message.Channel}'.", fatal: false, fromWorker: false);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(message.Data ?? string.Empty);
        }
        catch (FormatException)
        {
            ReportError(EngineErrorCodes.Protocol, $"The worker sent console data for {id} that is not base64.", fatal: false, fromWorker: false);
            return;
        }

        ((V86Console)GetConsole(id)).Deliver(bytes);
    }

    private void ReportState(EngineState reported, string? reason)
    {
        EngineStateChangedEventArgs? change;
        EngineErrorEventArgs? illegal = null;
        PendingOperation? completed = null;
        PendingOperation? failed = null;
        V86Console[] consoles = [];
        lock (_lock)
        {
            if (!EngineStateMachine.IsLegalReport(_state, reported))
            {
                illegal = new EngineErrorEventArgs(EngineErrorCodes.Protocol, $"The worker reported {Name(reported)} while the engine was {Name(_state)}.", fatal: false);
                _lastError = illegal;
            }

            SetStateUnsafe(reported, reason, out change);
            if (_pending is not null)
            {
                if (_pending.Target == reported)
                {
                    completed = _pending;
                    _pending = null;
                }
                else if (reported == EngineState.Stopped)
                {
                    failed = _pending;
                    _pending = null;
                }
            }

            if (reported == EngineState.Stopped && change is not null)
            {
                consoles = [.. _consoles.Values];
            }
        }

        // The guest's last partial line (a prompt) becomes a line when the engine stops.
        foreach (V86Console console in consoles)
        {
            console.FlushPendingLine();
        }

        completed?.Completion.TrySetResult();
        failed?.Completion.TrySetException(new EngineException(
            EngineErrorCodes.Stopped,
            failed.Error is { } error
                ? $"The engine stopped ({reason ?? "no reason"}) during the {Name(failed.Operation)}: {error.Code}: {error.Message}"
                : $"The engine stopped ({reason ?? "no reason"}) before the {Name(failed.Operation)} completed."));

        if (illegal is not null)
        {
            ErrorReported?.Invoke(this, illegal);
        }

        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
    }

    /// <summary>
    /// Records an error. An error the worker reports fails the pending operation when it is fatal
    /// or refuses the operation (<see cref="EngineErrorCodes.InvalidState"/>); an error found on
    /// this side (a message that does not parse) is only reported.
    /// </summary>
    private void ReportError(string code, string message, bool fatal, bool fromWorker)
    {
        var error = new EngineErrorEventArgs(code, message, fatal);
        PendingOperation? failed = null;
        EngineStateChangedEventArgs? change = null;
        lock (_lock)
        {
            _lastError = error;
            if (_pending is not null && fromWorker)
            {
                _pending.Error ??= error;
                if (fatal || code == EngineErrorCodes.InvalidState)
                {
                    failed = _pending;
                    _pending = null;
                    if (!fatal && failed.Operation == EngineOperation.Start)
                    {
                        // The worker refused to start: nothing was created, the engine is still stopped.
                        SetStateUnsafe(EngineState.Stopped, "refused", out change);
                    }
                }
            }
        }

        failed?.Completion.TrySetException(new EngineException(code, message));
        ErrorReported?.Invoke(this, error);
        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
    }

    private void SetStateUnsafe(EngineState state, string? reason, out EngineStateChangedEventArgs? change)
    {
        change = null;
        if (_state == state)
        {
            return;
        }

        change = new EngineStateChangedEventArgs(_state, state, reason);
        _state = state;
    }

    private static string Name(EngineOperation operation) => operation.ToString().ToLowerInvariant();

    private static string Name(EngineState state) => state.ToString().ToLowerInvariant();

    private sealed class PendingOperation(EngineOperation operation, EngineState target)
    {
        public EngineOperation Operation { get; } = operation;

        public EngineState Target { get; } = target;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The first error the worker reported while this operation was pending.</summary>
        public EngineErrorEventArgs? Error { get; set; }
    }
}
