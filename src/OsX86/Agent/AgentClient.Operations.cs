namespace OsX86.Agent;

/// <content>The operations of the protocol, one method each.</content>
public sealed partial class AgentClient
{
    /// <summary>The grace period of the agent between the signal and <c>cgroup.kill</c> when the caller gives none.</summary>
    public static readonly TimeSpan DefaultKillGrace = TimeSpan.FromSeconds(2);

    /// <summary>The time the agent leaves a command of <see cref="ExecInPrefixAsync"/> when the caller gives none.</summary>
    public static readonly TimeSpan DefaultExecTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The time the agent leaves the mounts to answer when the caller gives none.</summary>
    public static readonly TimeSpan DefaultMountsTimeout = TimeSpan.FromSeconds(8);

    private const int SignalTerminate = 15;

    /// <summary>Asks the agent whether it is there and what it tracks.</summary>
    public async Task<AgentStatus> PingAsync(CancellationToken cancellationToken)
    {
        AgentMessage reply = await SendAsync(new AgentRequest { Op = AgentProtocol.Operations.Ping }, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
        return AgentProtocol.ToStatus(reply);
    }

    /// <summary>Every process of the guest, with its prefix and its launch when it has one.</summary>
    public async Task<IReadOnlyList<ProcessInfo>> ListProcessesAsync(CancellationToken cancellationToken)
    {
        AgentMessage reply = await SendAsync(new AgentRequest { Op = AgentProtocol.Operations.ListProcesses }, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
        return AgentProtocol.ToProcesses(reply);
    }

    /// <summary>
    /// Starts a program as the guest user of a prefix, in a cgroup of its own (decision 206 A).
    /// Returns once the program is running; <see cref="LaunchResult.Exited"/> follows its end.
    /// </summary>
    /// <exception cref="AgentException"><see cref="AgentErrorCodes.UnknownPrefix"/>, <see cref="AgentErrorCodes.ProgramNotFound"/>, <see cref="AgentErrorCodes.ExecFailed"/>...</exception>
    public async Task<LaunchResult> LaunchAsync(LaunchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateProgram(options.Prefix, options.Program, options.Arguments, options.Environment, options.WorkingDirectory);
        var request = new AgentRequest
        {
            Op = AgentProtocol.Operations.Launch,
            Prefix = options.Prefix,
            Program = options.Program,
            Args = options.Arguments.Count > 0 ? options.Arguments : null,
            Env = options.Environment,
            Cwd = options.WorkingDirectory,
            Nproc = options.MaxProcesses,
            Log = options.Log ? true : null,
        };
        LaunchResult? result = null;
        await SendAsync(request, _options.RequestTimeout, reply =>
        {
            LaunchId id = AgentProtocol.RequireLaunch(reply.Launch);
            result = new LaunchResult(id, reply.Pid, reply.Uid, RegisterLaunch(id));
        }, AbandonLaunch, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Ends a launch (decision 206 A): <paramref name="signal"/> (SIGTERM by default) to every process
    /// of its cgroup, then <c>cgroup.kill</c> when something is left after <paramref name="grace"/>
    /// (<see cref="DefaultKillGrace"/> by default; zero kills at once). Returns when the launch is gone.
    /// </summary>
    /// <exception cref="AgentException"><see cref="AgentErrorCodes.UnknownLaunch"/> when the launch does not exist or is already over.</exception>
    public async Task<KillResult> KillAsync(LaunchId launch, TimeSpan? grace = null, int? signal = null, CancellationToken cancellationToken = default)
    {
        var request = new AgentRequest
        {
            Op = AgentProtocol.Operations.Kill,
            Launch = launch.ToString(),
            GraceMs = ToMilliseconds(grace, nameof(grace)),
            Signal = signal,
        };
        AgentMessage reply = await SendAsync(request, (grace ?? DefaultKillGrace) + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
        return new KillResult(reply.Forced, TimeSpan.FromMilliseconds(reply.Ms));
    }

    /// <summary>Administration: sends a signal to one process. A root process is refused unless <paramref name="allowRoot"/>.</summary>
    public Task<SignalResult> SignalProcessAsync(int processId, int signal = SignalTerminate, bool allowRoot = false, CancellationToken cancellationToken = default) =>
        SignalAsync(new AgentRequest
        {
            Op = AgentProtocol.Operations.Kill,
            Scope = AgentProtocol.Scopes.Process,
            Pid = processId,
            Signal = signal,
            AllowRoot = allowRoot ? true : null,
        }, cancellationToken);

    /// <summary>Administration: sends a signal to the process group of a process.</summary>
    public Task<SignalResult> SignalProcessGroupAsync(int processId, int signal = SignalTerminate, bool allowRoot = false, CancellationToken cancellationToken = default) =>
        SignalAsync(new AgentRequest
        {
            Op = AgentProtocol.Operations.Kill,
            Scope = AgentProtocol.Scopes.Group,
            Pid = processId,
            Signal = signal,
            AllowRoot = allowRoot ? true : null,
        }, cancellationToken);

    /// <summary>Administration: sends a signal to every process of the guest user of a prefix.</summary>
    public Task<SignalResult> SignalPrefixAsync(string prefix, int signal = SignalTerminate, CancellationToken cancellationToken = default)
    {
        ValidatePrefix(prefix);
        return SignalAsync(new AgentRequest { Op = AgentProtocol.Operations.Kill, Scope = AgentProtocol.Scopes.Prefix, Prefix = prefix, Signal = signal }, cancellationToken);
    }

    /// <summary>
    /// Administration: <c>cgroup.kill</c>, at once, of one launch (<paramref name="sequence"/>) or of
    /// everything a prefix has launched (<see langword="null"/>).
    /// </summary>
    public Task<SignalResult> KillCgroupAsync(string prefix, int? sequence = null, CancellationToken cancellationToken = default)
    {
        ValidatePrefix(prefix);
        return SignalAsync(new AgentRequest { Op = AgentProtocol.Operations.Kill, Scope = AgentProtocol.Scopes.Cgroup, Prefix = prefix, Seq = sequence }, cancellationToken);
    }

    /// <summary>The windows of the guest. Needs an X server in the image (work item 9).</summary>
    /// <exception cref="AgentException"><see cref="AgentErrorCodes.NoXServer"/> while the image has none.</exception>
    public async Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken)
    {
        AgentMessage reply = await SendAsync(new AgentRequest { Op = AgentProtocol.Operations.ListWindows }, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
        return AgentProtocol.ToWindows(reply);
    }

    /// <summary>
    /// The data mounts of the guest and whether each answers; a mount that does not answer within
    /// <paramref name="timeout"/> (<see cref="DefaultMountsTimeout"/> by default) comes back as <see cref="MountState.Hung"/>.
    /// </summary>
    public async Task<IReadOnlyList<MountInfo>> MountsAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var request = new AgentRequest { Op = AgentProtocol.Operations.Mounts, TimeoutMs = ToMilliseconds(timeout, nameof(timeout)) };
        AgentMessage reply = await SendAsync(request, (timeout ?? DefaultMountsTimeout) + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
        return AgentProtocol.ToMounts(reply);
    }

    /// <summary>
    /// Runs a non-interactive command as the guest user of a prefix and returns its output and exit
    /// code. The agent kills it at its timeout; other operations are served meanwhile.
    /// </summary>
    public async Task<ExecResult> ExecInPrefixAsync(ExecOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateProgram(options.Prefix, options.Program, options.Arguments, options.Environment, options.WorkingDirectory);
        var request = new AgentRequest
        {
            Op = AgentProtocol.Operations.ExecInPrefix,
            Prefix = options.Prefix,
            Program = options.Program,
            Args = options.Arguments.Count > 0 ? options.Arguments : null,
            Env = options.Environment,
            Cwd = options.WorkingDirectory,
            TimeoutMs = ToMilliseconds(options.Timeout, nameof(options)),
            MaxOutput = options.MaxOutputBytes,
        };
        AgentMessage reply = await SendAsync(request, (options.Timeout ?? DefaultExecTimeout) + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
        return AgentProtocol.ToExecResult(reply);
    }

    /// <summary>
    /// Opens a console for a prefix (decision 213 A): a pseudo-terminal with the user's shell, or
    /// the given program, on it. The handle carries the bytes both ways and the exit code.
    /// </summary>
    public async Task<ConsoleHandle> OpenConsoleAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSize(options.Columns, options.Rows);
        if (options.Program is null)
        {
            ValidatePrefix(options.Prefix);
            ValidateTexts([], options.Environment, options.WorkingDirectory);
        }
        else
        {
            ValidateProgram(options.Prefix, options.Program, options.Arguments, options.Environment, options.WorkingDirectory);
        }

        var request = new AgentRequest
        {
            Op = AgentProtocol.Operations.OpenConsole,
            Prefix = options.Prefix,
            Program = options.Program,
            Args = options.Program is not null && options.Arguments.Count > 0 ? options.Arguments : null,
            Env = options.Environment,
            Cwd = options.WorkingDirectory,
            Cols = options.Columns,
            Rows = options.Rows,
        };
        ConsoleHandle? console = null;
        lock (_lock)
        {
            _consolesOpening++;
        }

        try
        {
            await SendAsync(request, _options.RequestTimeout, reply =>
            {
                if (reply.Console <= 0)
                {
                    throw new AgentProtocolException(AgentErrorCodes.Protocol, $"The agent opened a console numbered {reply.Console}.");
                }

                console = new ConsoleHandle(this, AgentProtocol.RequireLaunch(reply.Launch), reply.Console, reply.Pid, options.Columns, options.Rows);
                RegisterConsole(console);
            }, AbandonConsole, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (--_consolesOpening == 0)
                {
                    _orphans.Clear(); // what is left belongs to no console that will ever be registered
                }
            }
        }

        return console!;
    }

    /// <summary>
    /// The agent accepted a launch whose caller had already given up (timeout, cancellation): nobody
    /// holds its identifier, so it is ended at once rather than left running unnamed in the guest.
    /// </summary>
    private void AbandonLaunch(AgentMessage reply)
    {
        if (!LaunchId.TryParse(reply.Launch, out LaunchId id))
        {
            return;
        }

        TaskCompletionSource<ProcessExit>? exited;
        lock (_lock)
        {
            _launches.Remove(id, out exited);
        }

        exited?.TrySetException(new AgentClosedException($"The launch {id} was abandoned by its caller."));
        _ = exited?.Task.Exception;
        _ = SendAndForgetAsync(new AgentRequest { Op = AgentProtocol.Operations.Kill, Launch = id.ToString(), GraceMs = 0 });
    }

    /// <summary>The same for a console whose caller gave up before the agent's reply.</summary>
    private void AbandonConsole(AgentMessage reply)
    {
        ConsoleHandle? console;
        lock (_lock)
        {
            _consoles.Remove(reply.Console, out console);
            _orphans.Remove(reply.Console);
        }

        console?.Fail(new AgentClosedException($"The console {reply.Console} was abandoned by its caller."));
        _ = SendAndForgetAsync(new AgentRequest { Op = AgentProtocol.Operations.CloseConsole, Console = reply.Console, GraceMs = 0 });
    }

    internal async ValueTask WriteConsoleAsync(int console, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ThrowIfClosedUnsafe();
        }

        byte[] frames = ConsoleFrame.EncodeData(console, data.Span);
        // One writer at a time: the frames of two consoles must not interleave on the port.
        await _consoleWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.WriteConsoleBytesAsync(frames, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _consoleWrite.Release();
        }
    }

    internal async Task ResizeConsoleAsync(int console, int columns, int rows, CancellationToken cancellationToken)
    {
        ValidateSize(columns, rows);
        var request = new AgentRequest { Op = AgentProtocol.Operations.ConsoleResize, Console = console, Cols = columns, Rows = rows };
        await SendAsync(request, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ConsoleCloseResult> CloseConsoleAsync(int console, TimeSpan? grace, IReadOnlyList<int>? keepProcessIds, CancellationToken cancellationToken)
    {
        var request = new AgentRequest
        {
            Op = AgentProtocol.Operations.CloseConsole,
            Console = console,
            GraceMs = ToMilliseconds(grace, nameof(grace)),
            Keep = keepProcessIds is { Count: > 0 } ? keepProcessIds : null,
        };
        AgentMessage reply = await SendAsync(request, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
        return new ConsoleCloseResult(reply.KeptCount, AgentProtocol.OptionalLaunch(reply.Kept));
    }

    private async Task<SignalResult> SignalAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        AgentMessage reply = await SendAsync(request, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
        return new SignalResult(reply.Count, reply.Signal, reply.Target ?? string.Empty);
    }

    private static int? ToMilliseconds(TimeSpan? duration, string parameterName)
    {
        if (duration is not { } value)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, AgentClientOptions.MaximumDelay, parameterName);
        return (int)value.TotalMilliseconds;
    }

    private static void ValidatePrefix(string prefix)
    {
        if (!LaunchId.IsValidPrefix(prefix))
        {
            throw new ArgumentException($"'{prefix}' is not a prefix name (lowercase letters, digits and dashes, 1 to {LaunchId.MaxPrefixLength} characters).", "options");
        }
    }

    private static void ValidateSize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 1000);
    }

    private static void ValidateProgram(string prefix, string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, string? workingDirectory)
    {
        ValidatePrefix(prefix);
        ArgumentException.ThrowIfNullOrEmpty(program, "options");
        ArgumentNullException.ThrowIfNull(arguments, "options");
        ValidateTexts(arguments.Append(program), environment, workingDirectory);
    }

    /// <summary>
    /// What the guest receives becomes C strings: a NUL would cut an argument short, so it is
    /// refused here rather than silently changing what runs.
    /// </summary>
    private static void ValidateTexts(IEnumerable<string> texts, IReadOnlyDictionary<string, string>? environment, string? workingDirectory)
    {
        texts = texts.Append(workingDirectory ?? string.Empty);
        if (environment is not null)
        {
            if (environment.Keys.Any(key => key.Length == 0 || key.Contains('=', StringComparison.Ordinal)))
            {
                throw new ArgumentException("An environment variable name is empty or contains '='.", "options");
            }

            texts = texts.Concat(environment.Keys).Concat(environment.Values);
        }

        if (texts.Any(text => text is null || text.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("The program, its arguments, its environment and its directory must not be null or contain a NUL character.", "options");
        }
    }
}
