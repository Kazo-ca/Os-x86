using OsX86.Agent;

namespace OsX86.Vm;

/// <summary>The delays and limits of the VM lifecycle (<see cref="VmManager"/>, <see cref="VmInstance"/>).</summary>
public sealed record VmManagerOptions
{
    /// <summary>
    /// How long a VM stays up after its last application closed before it is stopped (166 A says
    /// "as soon as"; the grace lets a shell that reopens at once, or a program that chains another,
    /// keep the VM: a boot costs 5 s for the Alpine image and 45 to 70 s for linux-base).
    /// </summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the agent is pinged while the VM runs; <see cref="TimeSpan.Zero"/> disables the watchdog.</summary>
    public TimeSpan AgentWatchdogInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long one watchdog ping may take (the agent answers in 3 to 8 ms; 10 s covers a guest under load).</summary>
    public TimeSpan AgentWatchdogTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many pings in a row may fail before the agent is declared lost.</summary>
    public int AgentWatchdogMisses { get; init; } = 2;

    /// <summary>The memory limit the administrator set (work item 7), or <see langword="null"/> for the default of 165 A.</summary>
    public int? MemoryLimitMiB { get; init; }

    /// <summary>The delays of the agent client (connection, requests).</summary>
    public AgentClientOptions Agent { get; init; } = new();

    /// <summary>How long a console is given to end on its own when it is closed (208 A: SIGHUP, then <c>cgroup.kill</c>).</summary>
    public TimeSpan ConsoleCloseGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the Wine session of a prefix (its wineserver and services, work item 10) is kept once
    /// its last program ended: a program run again soon finds it up. Zero ends it at once.
    /// </summary>
    public TimeSpan PrefixSessionGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the Wine session of a prefix may take to start (wineserver, then wineboot --init).</summary>
    public TimeSpan PrefixSessionStartTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>Checks the options; throws <see cref="ArgumentOutOfRangeException"/> naming the first invalid one.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Agent);
        Agent.Validate();
        if (StopGrace < TimeSpan.Zero || StopGrace > AgentClientOptions.MaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(StopGrace), StopGrace, "The grace must be zero or positive.");
        }

        if (AgentWatchdogInterval < TimeSpan.Zero || AgentWatchdogInterval > AgentClientOptions.MaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentWatchdogInterval), AgentWatchdogInterval, "The interval must be zero (disabled) or positive.");
        }

        if (AgentWatchdogTimeout <= TimeSpan.Zero || AgentWatchdogTimeout > AgentClientOptions.MaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentWatchdogTimeout), AgentWatchdogTimeout, "The timeout must be positive.");
        }

        if (AgentWatchdogMisses < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentWatchdogMisses), AgentWatchdogMisses, "At least one miss is needed.");
        }

        if (MemoryLimitMiB is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryLimitMiB), MemoryLimitMiB, "The limit must be positive.");
        }

        if (ConsoleCloseGrace < TimeSpan.Zero || ConsoleCloseGrace > AgentClientOptions.MaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(ConsoleCloseGrace), ConsoleCloseGrace, "The grace must be zero or positive.");
        }

        if (PrefixSessionGrace < TimeSpan.Zero || PrefixSessionGrace > AgentClientOptions.MaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(PrefixSessionGrace), PrefixSessionGrace, "The grace must be zero or positive.");
        }

        if (PrefixSessionStartTimeout < TimeSpan.FromSeconds(1) || PrefixSessionStartTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PrefixSessionStartTimeout), PrefixSessionStartTimeout, "The timeout is 1 s to 1 h.");
        }
    }
}

/// <summary>What a child of a VM is (decision 207 A: each launch is a Kazo child process of the VM; a console is a child like any other).</summary>
public enum VmChildKind
{
    /// <summary>A program launched through the agent.</summary>
    Program = 0,

    /// <summary>A console (a pseudo-terminal with a shell) opened through the agent.</summary>
    Console = 1,

    /// <summary>The Wine session of a prefix (work item 10): its wineserver and services, kept between its programs; not an application.</summary>
    Session = 2,
}

/// <summary>One child of a running VM: a launch of the agent, registered at <c>launch</c>, removed at <c>exit</c> (207 A).</summary>
/// <param name="Launch">The launch identifier (206 A: never a pid).</param>
/// <param name="Kind">Program or console.</param>
/// <param name="Started">When it was registered.</param>
public sealed record VmChild(LaunchId Launch, VmChildKind Kind, DateTimeOffset Started);

/// <summary>Arguments of <see cref="VmManager.AgentLost"/>: the watchdog of a VM gave up on its agent.</summary>
public sealed class VmAgentLostEventArgs(Guid vmId, int missedPings) : EventArgs
{
    /// <summary>The VM.</summary>
    public Guid VmId { get; } = vmId;

    /// <summary>How many pings in a row went unanswered.</summary>
    public int MissedPings { get; } = missedPings;
}

/// <summary>
/// One application that keeps a VM up (decision 166 A): a program, a shell, a console, or the manual
/// hold of <c>x86 vm start</c>. Disposing it releases the VM, which stops after the grace when it was
/// the last one.
/// </summary>
public sealed class VmApplication : IDisposable
{
    private readonly VmInstance _instance;
    private int _released;

    internal VmApplication(VmInstance instance, string reason, long generation)
    {
        _instance = instance;
        Reason = reason;
        Generation = generation;
    }

    /// <summary>Why the VM is held: <c>shell</c>, <c>console</c>, <c>program</c>, <c>manual</c>.</summary>
    public string Reason { get; }

    /// <summary>Which run of the VM the application belongs to; a stop ends every application of the run.</summary>
    internal long Generation { get; }

    /// <summary>Whether the application was released.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>Releases the VM. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _instance.Release(this);
        }
    }
}
