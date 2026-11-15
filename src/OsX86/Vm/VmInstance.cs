using OsX86.Agent;
using OsX86.Engine;
using OsX86.Interface;

namespace OsX86.Vm;

/// <summary>
/// The runtime of one VM (plan §1.2, decisions 166 A and 207 A): started on demand by its first
/// application, stopped after the grace when its last one closes, or by hand; one engine per run;
/// the agent client connected once the engine runs (212 A: an image without agent runs without
/// children, its serial console is the fallback when the profile declares one); the children
/// (launches and consoles) registered at <c>launch</c> and removed at <c>exit</c>; a watchdog that
/// pings the agent and declares it lost. Every lifecycle operation runs under one lock, so a start
/// and a stop never overlap; the state and its changes are reported through events.
/// </summary>
public sealed class VmInstance
{
    private readonly VmManager _manager;
    private readonly TimeProvider _time;
    private readonly VmManagerOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _lock = new();
    private readonly List<VmChild> _children = [];
    private readonly List<SerialVmConsole> _serialConsoles = [];
    private VmRecord _record;
    private VmState _state;
    private int _applications;
    private bool _held;
    private long _generation;
    private ITimer? _graceTimer;
    private IEngine? _engine;
    private AgentClient? _agent;
    private CancellationTokenSource? _runStop;
    private bool? _hasAgent;
    private bool _agentLost;
    private int _missedPings;
    private TimeSpan? _lastPing;

    internal VmInstance(Guid ownerId, VmRecord record, VmManager manager, VmManagerOptions options, TimeProvider time)
    {
        OwnerId = ownerId;
        _record = record;
        _manager = manager;
        _options = options;
        _time = time;
    }

    /// <summary>The stable identifier of the user the VM belongs to (190 A).</summary>
    public Guid OwnerId { get; }

    /// <summary>Raised on every state change.</summary>
    internal event EventHandler<VmStateChangedEventArgs>? StateChanged;

    /// <summary>Raised once per run when the watchdog gives up on the agent.</summary>
    internal event EventHandler<VmAgentLostEventArgs>? AgentLost;

    /// <summary>The VM as registered; replaced on rename.</summary>
    public VmRecord Record
    {
        get
        {
            lock (_lock)
            {
                return _record;
            }
        }

        internal set
        {
            lock (_lock)
            {
                _record = value;
            }
        }
    }

    /// <summary>The stable identifier of the VM.</summary>
    public Guid Id => Record.Id;

    /// <summary>The current state.</summary>
    public VmState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>How many applications hold the VM (the manual hold counts as one).</summary>
    public int Applications
    {
        get
        {
            lock (_lock)
            {
                return _applications;
            }
        }
    }

    /// <summary>Whether <c>x86 vm start</c> holds the VM (72.6: it then stays up until <c>x86 vm stop</c>).</summary>
    public bool IsHeld
    {
        get
        {
            lock (_lock)
            {
                return _held;
            }
        }
    }

    /// <summary>Whether the guest agent answered on this run; <see langword="null"/> while stopped or starting.</summary>
    public bool? HasAgent
    {
        get
        {
            lock (_lock)
            {
                return _hasAgent;
            }
        }
    }

    /// <summary>Whether the watchdog declared the agent lost on this run.</summary>
    public bool IsAgentLost
    {
        get
        {
            lock (_lock)
            {
                return _agentLost;
            }
        }
    }

    /// <summary>The round trip of the last watchdog ping, when one was measured.</summary>
    public TimeSpan? LastPingRoundTrip
    {
        get
        {
            lock (_lock)
            {
                return _lastPing;
            }
        }
    }

    /// <summary>The children of the VM (207 A): launches and consoles still running in the guest.</summary>
    public IReadOnlyList<VmChild> Children
    {
        get
        {
            lock (_lock)
            {
                return [.. _children];
            }
        }
    }

    /// <summary>The engine of the current run, for the host that attaches a display; <see langword="null"/> while stopped.</summary>
    public IEngine? Engine
    {
        get
        {
            lock (_lock)
            {
                return _engine;
            }
        }
    }

    /// <summary>The VM as a client sees it.</summary>
    public VmSummary Summary => Record.ToSummary(State);

    /// <summary>
    /// Takes an application on the VM (166 A), starting it when it is stopped: the budget is checked
    /// (165 A), the engine created and booted, the agent connected. Several callers may wait on the
    /// same boot. The application keeps the VM up until it is disposed.
    /// </summary>
    /// <exception cref="VmMemoryBudgetExceededException">The budget would be exceeded.</exception>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.EngineUnavailable"/>, <see cref="X86ErrorCodes.ImageUnavailable"/>, <see cref="X86ErrorCodes.StartFailed"/>.</exception>
    public async ValueTask<VmApplication> AcquireAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != VmState.Running)
            {
                await StartUnderLockAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_lock)
            {
                _applications++;
                CancelGraceUnsafe();
                return new VmApplication(this, reason, _generation);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Starts the VM by hand and keeps it up until <see cref="StopAsync"/> (72.6). A no-op when already held.</summary>
    public async ValueTask HoldAsync(CancellationToken cancellationToken)
    {
        VmApplication application = await AcquireAsync("manual", cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_held)
            {
                _applications--; // the hold is counted once
                return;
            }

            _held = true;
        }

        GC.KeepAlive(application); // the hold is released by StopAsync, not by disposing this handle
    }

    /// <summary>
    /// Stops the VM. Without <paramref name="force"/>, refuses while applications hold it (the manual
    /// hold is not one of them: it is what this call releases); with it, every application and
    /// console is closed and the engine torn down at once. A no-op when stopped.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmBusy"/>.</exception>
    public async ValueTask StopAsync(bool force, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_state == VmState.Stopped)
                {
                    return;
                }

                int others = _applications - (_held ? 1 : 0);
                if (!force && others > 0)
                {
                    throw new X86Exception(X86ErrorCodes.VmBusy, $"{others} application(s) still use this virtual machine; stop them, or stop it with --force.");
                }

                _held = false;
            }

            await StopUnderLockAsync("stopped").ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Opens a console (213 A), starting the VM when needed: through the agent, a pseudo-terminal
    /// with the shell of the prefix's user; without agent, the serial console when the profile
    /// declares one (212 A). The console is an application of the VM.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.NoConsole"/>, <see cref="X86ErrorCodes.NoPrefix"/>, <see cref="X86ErrorCodes.AgentError"/>, <see cref="X86ErrorCodes.AgentLost"/>, or a start failure.</exception>
    public async ValueTask<IVmConsole> OpenConsoleAsync(VmConsoleOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        VmApplication application = await AcquireAsync("console", cancellationToken).ConfigureAwait(false);
        try
        {
            AgentClient? agent;
            IEngine? engine;
            bool hasAgent;
            bool agentLost;
            lock (_lock)
            {
                agent = _agent;
                engine = _engine;
                hasAgent = _hasAgent == true;
                agentLost = _agentLost;
            }

            if (hasAgent && agent is not null)
            {
                if (agentLost)
                {
                    throw new X86Exception(X86ErrorCodes.AgentLost, "The guest agent stopped answering; stop the virtual machine and start it again.");
                }

                string prefix = options.Prefix ?? Record.Profile.Prefix
                    ?? throw new X86Exception(X86ErrorCodes.NoPrefix, "No prefix was given and the profile of this virtual machine has no default one: use --prefix.");
                ConsoleHandle handle;
                try
                {
                    handle = await agent.OpenConsoleAsync(new ConsoleOptions { Prefix = prefix, Columns = options.Columns, Rows = options.Rows }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw VmConsoleBase.Translate(exception);
                }

                RegisterChild(handle.Launch, VmChildKind.Console, handle.Exited);
                return new AgentVmConsole(Id, application, handle, _options.ConsoleCloseGrace);
            }

            if (Record.Profile.SerialConsole && engine is not null)
            {
                var serial = new SerialVmConsole(Id, application, engine.GetConsole(ConsoleId.Serial0));
                lock (_lock)
                {
                    _serialConsoles.Add(serial);
                }

                _ = serial.Exited.ContinueWith(_ => { lock (_lock) { _serialConsoles.Remove(serial); } }, TaskScheduler.Default);
                return serial;
            }

            throw new X86Exception(X86ErrorCodes.NoConsole, "This image has no guest agent and its profile declares no serial port: no console can be opened (212 A).");
        }
        catch
        {
            application.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Launches a program through the agent (206 A, 207 A): registered as a child of the VM until its
    /// <c>exit</c>. The launch is an application of the VM; the returned handle releases it (the
    /// program itself keeps running until it ends or is killed).
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.NoConsole"/> when the image has no agent, <see cref="X86ErrorCodes.AgentError"/>, <see cref="X86ErrorCodes.AgentLost"/>.</exception>
    public async ValueTask<(LaunchResult Launch, VmApplication Application)> LaunchAsync(LaunchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        VmApplication application = await AcquireAsync("program", cancellationToken).ConfigureAwait(false);
        try
        {
            AgentClient? agent;
            bool agentLost;
            lock (_lock)
            {
                agent = _hasAgent == true ? _agent : null;
                agentLost = _agentLost;
            }

            if (agent is null)
            {
                throw new X86Exception(X86ErrorCodes.NoConsole, "This image has no guest agent: nothing can be launched in it (212 A).");
            }

            if (agentLost)
            {
                throw new X86Exception(X86ErrorCodes.AgentLost, "The guest agent stopped answering; stop the virtual machine and start it again.");
            }

            LaunchResult launch;
            try
            {
                launch = await agent.LaunchAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw VmConsoleBase.Translate(exception);
            }

            RegisterChild(launch.Id, VmChildKind.Program, launch.Exited);
            _ = launch.Exited.ContinueWith(_ => application.Dispose(), TaskScheduler.Default);
            return (launch, application);
        }
        catch
        {
            application.Dispose();
            throw;
        }
    }

    /// <summary>Pings the agent once and records the round trip; what the watchdog does, on demand.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.NoConsole"/> when the VM has no agent or is stopped.</exception>
    public async ValueTask<TimeSpan> PingAgentAsync(CancellationToken cancellationToken)
    {
        AgentClient agent;
        lock (_lock)
        {
            agent = _hasAgent == true && _agent is not null ? _agent : throw new X86Exception(X86ErrorCodes.NoConsole, "The virtual machine has no agent to ping.");
        }

        long start = _time.GetTimestamp();
        try
        {
            await agent.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw VmConsoleBase.Translate(exception);
        }

        TimeSpan elapsed = _time.GetElapsedTime(start);
        lock (_lock)
        {
            _lastPing = elapsed;
        }

        return elapsed;
    }

    internal void Release(VmApplication application)
    {
        lock (_lock)
        {
            if (application.Generation != _generation || _applications == 0)
            {
                return; // a run that already ended
            }

            _applications--;
            if (_applications > 0 || _state != VmState.Running)
            {
                return;
            }

            CancelGraceUnsafe();
            long generation = _generation;
            if (_options.StopGrace == TimeSpan.Zero)
            {
                _ = StopIfIdleAsync(generation);
            }
            else
            {
                _graceTimer = _time.CreateTimer(_ => _ = StopIfIdleAsync(generation), null, _options.StopGrace, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task StopIfIdleAsync(long generation)
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_lock)
                {
                    if (_generation != generation || _applications > 0 || _held || _state != VmState.Running)
                    {
                        return;
                    }
                }

                await StopUnderLockAsync("last application closed").ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (Exception exception)
        {
            _manager.ReportBackgroundError(this, exception);
        }
    }

    private async Task StartUnderLockAsync(CancellationToken cancellationToken)
    {
        VmRecord record = Record;
        BootImage boot = await _manager.Images.ResolveAsync(record.Profile.Image, record.Profile.ImageVersion, cancellationToken).ConfigureAwait(false);

        // The check and the move to Starting are one step for the whole machine: two VMs that start
        // together cannot both pass a budget that only one of them fits in. No engine exists before.
        await _manager.BudgetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manager.CheckBudget(this);
            lock (_lock)
            {
                _hasAgent = null;
                _agentLost = false;
                _missedPings = 0;
                _lastPing = null;
                _generation++;
            }

            SetState(VmState.Starting);
        }
        finally
        {
            _manager.BudgetGate.Release();
        }

        IEngine engine;
        try
        {
            engine = _manager.Engines.Create();
        }
        catch
        {
            SetState(VmState.Stopped);
            throw;
        }

        var runStop = new CancellationTokenSource();
        lock (_lock)
        {
            _engine = engine;
            _runStop = runStop;
        }

        engine.StateChanged += OnEngineStateChanged;
        try
        {
            var options = new EngineStartOptions
            {
                KernelPath = boot.KernelPath,
                InitrdPath = boot.InitrdPath,
                KernelCommandLine = boot.KernelCommandLine,
                MemoryMiB = record.Profile.MemoryMiB,
                VirtioConsole = true,
            };
            await engine.StartAsync(options, cancellationToken).ConfigureAwait(false);
            await ConnectAgentAsync(engine, runStop.Token, cancellationToken).ConfigureAwait(false);
            SetState(VmState.Running);
        }
        catch (Exception exception)
        {
            await TearDownAsync("start failed").ConfigureAwait(false);
            if (exception is OperationCanceledException or X86Exception)
            {
                throw;
            }

            throw exception is EngineException engineError
                ? new X86Exception(X86ErrorCodes.StartFailed, $"The virtual machine did not start ({engineError.Code}): {engineError.Message}", engineError)
                : new X86Exception(X86ErrorCodes.StartFailed, $"The virtual machine did not start: {exception.Message}", exception);
        }
    }

    private async Task ConnectAgentAsync(IEngine engine, CancellationToken runStop, CancellationToken cancellationToken)
    {
        var agent = new AgentClient(EngineAgentChannel.For(engine), _options.Agent);
        try
        {
            await agent.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentTimeoutException timeout) when (timeout.Code == AgentErrorCodes.NotReady)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _hasAgent = false;
            }

            return;
        }
        catch
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_lock)
        {
            _agent = agent;
            _hasAgent = true;
        }

        _ = WatchEventsAsync(agent, runStop);
        if (_options.AgentWatchdogInterval > TimeSpan.Zero)
        {
            _ = WatchdogAsync(agent, runStop);
        }
    }

    private async Task WatchEventsAsync(AgentClient agent, CancellationToken runStop)
    {
        try
        {
            await foreach (AgentEvent agentEvent in agent.ReadEventsAsync(runStop).ConfigureAwait(false))
            {
                if (agentEvent is AgentReadyEvent)
                {
                    // The agent restarted: its tables are gone, so are the children (the client failed their handles).
                    lock (_lock)
                    {
                        _children.Clear();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException || runStop.IsCancellationRequested)
        {
            // The run ended.
        }
        catch (Exception exception)
        {
            _manager.ReportBackgroundError(this, exception);
        }
    }

    private async Task WatchdogAsync(AgentClient agent, CancellationToken runStop)
    {
        try
        {
            while (!runStop.IsCancellationRequested)
            {
                await Task.Delay(_options.AgentWatchdogInterval, _time, runStop).ConfigureAwait(false);
                using var deadline = new CancellationTokenSource(_options.AgentWatchdogTimeout, _time);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runStop, deadline.Token);
                long start = _time.GetTimestamp();
                try
                {
                    await agent.PingAsync(timeout.Token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _missedPings = 0;
                        _lastPing = _time.GetElapsedTime(start);
                    }
                }
                catch (Exception) when (!runStop.IsCancellationRequested)
                {
                    int missed;
                    lock (_lock)
                    {
                        missed = ++_missedPings;
                        if (missed < _options.AgentWatchdogMisses)
                        {
                            continue;
                        }

                        _agentLost = true;
                        _children.Clear();
                    }

                    AgentLost?.Invoke(this, new VmAgentLostEventArgs(Id, missed));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
        catch (Exception exception)
        {
            _manager.ReportBackgroundError(this, exception);
        }
    }

    private void OnEngineStateChanged(object? sender, EngineStateChangedEventArgs e)
    {
        if (e.Current != EngineState.Stopped || sender is not IEngine engine)
        {
            return;
        }

        bool ours;
        VmState state;
        lock (_lock)
        {
            ours = ReferenceEquals(engine, _engine);
            state = _state;
        }

        if (ours && state == VmState.Running)
        {
            // The guest died (crash, fatal error of the worker): the run is over.
            _ = Task.Run(async () =>
            {
                await _lifecycle.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool still;
                    lock (_lock)
                    {
                        still = ReferenceEquals(engine, _engine) && _state == VmState.Running;
                    }

                    if (still)
                    {
                        await StopUnderLockAsync(e.Reason ?? "engine stopped").ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    _manager.ReportBackgroundError(this, exception);
                }
                finally
                {
                    _lifecycle.Release();
                }
            });
        }
    }

    private async Task StopUnderLockAsync(string reason)
    {
        SetState(VmState.Stopping);
        await TearDownAsync(reason).ConfigureAwait(false);
    }

    /// <summary>Ends the run: the children fail, the agent client closes, the engine stops and is released; the state becomes stopped.</summary>
    private async Task TearDownAsync(string reason)
    {
        IEngine? engine;
        AgentClient? agent;
        CancellationTokenSource? runStop;
        List<SerialVmConsole> serials;
        lock (_lock)
        {
            engine = _engine;
            agent = _agent;
            runStop = _runStop;
            serials = [.. _serialConsoles];
            _engine = null;
            _agent = null;
            _runStop = null;
            _children.Clear();
            _serialConsoles.Clear();
            _applications = 0;
            _held = false;
            CancelGraceUnsafe();
        }

        runStop?.Cancel();
        foreach (SerialVmConsole serial in serials)
        {
            serial.Fail(new X86Exception(X86ErrorCodes.ConsoleClosed, $"The virtual machine stopped ({reason})."));
        }

        if (agent is not null)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        if (engine is not null)
        {
            engine.StateChanged -= OnEngineStateChanged;
            try
            {
                await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (EngineException)
            {
                // Already stopped or dead: what we want.
            }

            await engine.DisposeAsync().ConfigureAwait(false);
        }

        runStop?.Dispose();
        SetState(VmState.Stopped);
    }

    private void RegisterChild(LaunchId launch, VmChildKind kind, Task<ProcessExit> exited)
    {
        var child = new VmChild(launch, kind, _time.GetUtcNow());
        lock (_lock)
        {
            _children.Add(child);
        }

        _ = exited.ContinueWith(
            _ =>
            {
                lock (_lock)
                {
                    _children.Remove(child);
                }
            },
            TaskScheduler.Default);
    }

    private void SetState(VmState state)
    {
        VmState previous;
        lock (_lock)
        {
            previous = _state;
            if (previous == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, new VmStateChangedEventArgs(Id, previous, state));
    }

    private void CancelGraceUnsafe()
    {
        _graceTimer?.Dispose();
        _graceTimer = null;
    }
}
