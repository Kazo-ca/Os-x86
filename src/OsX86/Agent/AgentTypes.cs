using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OsX86.Agent;

/// <summary>
/// Names one launch of the guest agent: a program started for a prefix, with every process it
/// creates (decision 206 A: one launch is one cgroup, and Os-x86 exposes this identifier, never a
/// pid). The text form is <c>&lt;prefix&gt;/&lt;sequence&gt;</c>, the path of the cgroup under the agent's root.
/// </summary>
/// <param name="Prefix">The prefix the program runs for (lowercase letters, digits and dashes, 1 to 16 characters).</param>
/// <param name="Sequence">The number the agent gave the launch, unique for the life of the agent.</param>
public readonly record struct LaunchId(string Prefix, int Sequence)
{
    /// <summary>The longest prefix name the agent accepts.</summary>
    public const int MaxPrefixLength = 16;

    /// <summary>Whether <paramref name="prefix"/> is a name the agent accepts.</summary>
    public static bool IsValidPrefix([NotNullWhen(true)] string? prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > MaxPrefixLength || prefix[0] == '-')
        {
            return false;
        }

        foreach (char c in prefix)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses <c>&lt;prefix&gt;/&lt;sequence&gt;</c>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out LaunchId id)
    {
        id = default;
        if (text is null)
        {
            return false;
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !IsValidPrefix(text[..slash]))
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence) || sequence <= 0)
        {
            return false;
        }

        id = new LaunchId(text[..slash], sequence);
        return true;
    }

    /// <summary>Parses <c>&lt;prefix&gt;/&lt;sequence&gt;</c>.</summary>
    /// <exception cref="FormatException">The text is not a launch identifier.</exception>
    public static LaunchId Parse(string text) =>
        TryParse(text, out LaunchId id) ? id : throw new FormatException($"'{text}' is not a launch identifier (<prefix>/<sequence>).");

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Prefix}/{Sequence}");
}

/// <summary>What the agent says about itself (the <c>ping</c> operation).</summary>
/// <param name="ProtocolVersion">The version of the protocol the agent speaks.</param>
/// <param name="Uptime">How long the guest has been up.</param>
/// <param name="ResidentKiB">The resident memory of the agent.</param>
/// <param name="Launches">How many launches the agent tracks.</param>
/// <param name="Consoles">How many consoles are open.</param>
/// <param name="HasCgroups">Whether the guest has cgroup v2 (without it a launch is only a process group).</param>
public sealed record AgentStatus(int ProtocolVersion, TimeSpan Uptime, long ResidentKiB, int Launches, int Consoles, bool HasCgroups);

/// <summary>A program to start in the guest (the <c>launch</c> operation). Never a shell line: a program and its arguments.</summary>
public sealed record LaunchOptions
{
    /// <summary>The prefix whose guest user runs the program.</summary>
    public required string Prefix { get; init; }

    /// <summary>The program: a path, or a name looked up in the guest's <c>PATH</c>.</summary>
    public required string Program { get; init; }

    /// <summary>The arguments after the program name.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Environment variables added to the minimal environment of the agent (<c>LD_*</c> is refused).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The working directory; the home of the prefix user when <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The limit of processes of the prefix user (<c>RLIMIT_NPROC</c>); the agent's default (256) when <see langword="null"/>.</summary>
    public int? MaxProcesses { get; init; }

    /// <summary>Whether the output goes to a log under <c>/run/osx86</c> in the guest instead of nowhere.</summary>
    public bool Log { get; init; }
}

/// <summary>How the main process of a launch or of a console ended.</summary>
/// <param name="Code">The exit code; -1 when the process was ended by a signal.</param>
/// <param name="Signal">The signal that ended the process; 0 when it exited by itself.</param>
public readonly record struct ProcessExit(int Code, int Signal)
{
    /// <summary>Whether the process was ended by a signal.</summary>
    public bool Signaled => Signal != 0;
}

/// <summary>A launch the agent accepted.</summary>
/// <param name="Id">The identifier to keep (206 A).</param>
/// <param name="ProcessId">The pid of the process started; informational (a launcher may exec or fork something else).</param>
/// <param name="UserId">The uid of the prefix user.</param>
/// <param name="Exited">Completes when the main process of the launch exits; fails when the client closes first.</param>
public sealed record LaunchResult(LaunchId Id, int ProcessId, int UserId, Task<ProcessExit> Exited);

/// <summary>What a kill by launch did.</summary>
/// <param name="Forced">Whether the grace period ran out and the agent had to use <c>cgroup.kill</c>.</param>
/// <param name="Duration">How long the agent took, measured in the guest.</param>
public sealed record KillResult(bool Forced, TimeSpan Duration);

/// <summary>What an administrative kill (by pid, group, prefix or cgroup) did.</summary>
/// <param name="Count">How many processes were signalled.</param>
/// <param name="Signal">The signal sent.</param>
/// <param name="Target">What the agent aimed at, in its own words.</param>
public sealed record SignalResult(int Count, int Signal, string Target);

/// <summary>One process of the guest (the <c>list-processes</c> operation).</summary>
/// <param name="ProcessId">The pid.</param>
/// <param name="ParentProcessId">The parent's pid.</param>
/// <param name="ProcessGroupId">The process group.</param>
/// <param name="UserId">The real uid.</param>
/// <param name="State">The state letter of <c>/proc</c> (R, S, Z...).</param>
/// <param name="Prefix">The prefix of the owner, when the owner is a prefix user.</param>
/// <param name="Launch">The launch whose cgroup holds the process, when there is one.</param>
/// <param name="Name">The short name of the program.</param>
/// <param name="CommandLine">The command line, arguments separated by spaces.</param>
/// <param name="Cgroup">The cgroup path, relative to the cgroup root.</param>
public sealed record ProcessInfo(int ProcessId, int ParentProcessId, int ProcessGroupId, int UserId, string State, string? Prefix,
    LaunchId? Launch, string Name, string CommandLine, string Cgroup);

/// <summary>What the agent found when it looked at a mount.</summary>
public enum MountState
{
    /// <summary>The mount answers.</summary>
    Ok = 0,

    /// <summary>The mount answers with an error (a share that went away answers <c>EIO</c>, trial E7).</summary>
    Error = 1,

    /// <summary>The mount did not answer before the deadline.</summary>
    Hung = 2,
}

/// <summary>One data mount of the guest (the <c>mounts</c> operation).</summary>
/// <param name="Path">The mount point.</param>
/// <param name="FileSystem">The file system type (<c>ext4</c>, <c>9p</c>, <c>tmpfs</c>...).</param>
/// <param name="Device">The device or source.</param>
/// <param name="State">Whether it answers.</param>
/// <param name="Entries">The entries of its root directory; -1 when it could not be read.</param>
/// <param name="TotalBytes">Its size.</param>
/// <param name="FreeBytes">The space left for an ordinary user.</param>
/// <param name="ErrorNumber">The <c>errno</c> of the failure; 0 when <see cref="State"/> is <see cref="MountState.Ok"/>.</param>
/// <param name="Error">The failure in words; empty when there is none.</param>
public sealed record MountInfo(string Path, string FileSystem, string Device, MountState State, int Entries, long TotalBytes, long FreeBytes,
    int ErrorNumber, string Error);

/// <summary>A non-interactive command to run for a prefix (the <c>exec-in-prefix</c> operation).</summary>
public sealed record ExecOptions
{
    /// <summary>The prefix whose guest user runs the command.</summary>
    public required string Prefix { get; init; }

    /// <summary>The program: a path, or a name looked up in the guest's <c>PATH</c>.</summary>
    public required string Program { get; init; }

    /// <summary>The arguments after the program name.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Environment variables added to the minimal environment of the agent.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The working directory; the home of the prefix user when <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>After how long the agent kills the command; the agent's default (30 s) when <see langword="null"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>How many bytes of output the agent keeps (at most 1 MiB); the agent's default (64 KiB) when <see langword="null"/>.</summary>
    public int? MaxOutputBytes { get; init; }
}

/// <summary>The outcome of <see cref="ExecOptions"/>.</summary>
/// <param name="Exit">How the command ended.</param>
/// <param name="TimedOut">Whether the agent killed it at its deadline.</param>
/// <param name="Truncated">Whether it wrote more than the agent kept.</param>
/// <param name="Output">Its standard output and error, interleaved; bytes that are not UTF-8 arrive as U+FFFD.</param>
/// <param name="Duration">How long it ran, measured in the guest.</param>
public sealed record ExecResult(ProcessExit Exit, bool TimedOut, bool Truncated, string Output, TimeSpan Duration);

/// <summary>A console to open for a prefix (the <c>open-console</c> operation, decision 213 A: one pty per console).</summary>
public sealed record ConsoleOptions
{
    /// <summary>The prefix whose guest user owns the console.</summary>
    public required string Prefix { get; init; }

    /// <summary>The program on the terminal; the interactive shell of the prefix user when <see langword="null"/>.</summary>
    public string? Program { get; init; }

    /// <summary>The arguments after the program name (ignored without <see cref="Program"/>).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Environment variables added to the minimal environment of the agent (<c>TERM</c> is <c>xterm</c>).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The working directory; the home of the prefix user when <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The width of the terminal in columns (1 to 1000).</summary>
    public int Columns { get; init; } = 80;

    /// <summary>The height of the terminal in rows (1 to 1000).</summary>
    public int Rows { get; init; } = 25;
}

/// <summary>A window of the guest (the <c>list-windows</c> operation; needs an X server in the image, work item 9).</summary>
/// <param name="WindowId">The X window identifier.</param>
/// <param name="ProcessId">The pid that owns the window.</param>
/// <param name="Launch">The launch that owns the window, when there is one.</param>
/// <param name="Title">The window title.</param>
public sealed record WindowInfo(string WindowId, int ProcessId, LaunchId? Launch, string Title);

/// <summary>Something the agent reports without being asked.</summary>
public abstract record AgentEvent;

/// <summary>The agent started (or restarted) and listens.</summary>
/// <param name="ProtocolVersion">The version of the protocol it speaks.</param>
/// <param name="ProcessId">Its pid in the guest.</param>
/// <param name="HasCgroups">Whether the guest has cgroup v2.</param>
public sealed record AgentReadyEvent(int ProtocolVersion, int ProcessId, bool HasCgroups) : AgentEvent;

/// <summary>The main process of a launch exited.</summary>
/// <param name="Launch">The launch.</param>
/// <param name="Exit">Its exit code or signal.</param>
/// <param name="RemainingProcesses">The processes still in the launch's cgroup (a launcher may leave children behind).</param>
public sealed record LaunchExitedEvent(LaunchId Launch, ProcessExit Exit, int RemainingProcesses) : AgentEvent;

/// <summary>The main process of a console exited; the agent closes what remains of it (decision 208 A).</summary>
/// <param name="Launch">The launch of the console.</param>
/// <param name="Console">The console number.</param>
/// <param name="Exit">Its exit code or signal.</param>
/// <param name="RemainingProcesses">The processes still in the console's cgroup at that moment.</param>
public sealed record ConsoleClosedEvent(LaunchId Launch, int Console, ProcessExit Exit, int RemainingProcesses) : AgentEvent;

/// <summary>A guest program asked to open a URL (<c>osx86-open-url</c>); the host decides what to do with it.</summary>
/// <param name="Prefix">The prefix of the caller, read by the agent from the socket (<c>SO_PEERCRED</c>), never from the message.</param>
/// <param name="UserId">The uid of the caller.</param>
/// <param name="ProcessId">The pid of the caller.</param>
/// <param name="Url">The text the program gave; not validated.</param>
public sealed record OpenUrlEvent(string? Prefix, int UserId, int ProcessId, string Url) : AgentEvent;

/// <summary>The agent or the channel misbehaved: an error without a request, a line that is not of the protocol.</summary>
/// <param name="Code">One of <see cref="AgentErrorCodes"/>.</param>
/// <param name="Message">What happened.</param>
public sealed record AgentFaultEvent(string Code, string Message) : AgentEvent;
