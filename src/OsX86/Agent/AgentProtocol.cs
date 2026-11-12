using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsX86.Agent;

/// <summary>
/// A request to the agent: <c>{"id":N,"op":"...", ...}</c>. One class for every operation; the
/// fields an operation does not use stay <see langword="null"/> and are not written.
/// </summary>
internal sealed class AgentRequest
{
    public long Id { get; set; }

    public required string Op { get; init; }

    public string? Prefix { get; init; }

    public string? Program { get; init; }

    public IReadOnlyList<string>? Args { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? Cwd { get; init; }

    public int? Nproc { get; init; }

    public bool? Log { get; init; }

    public string? Launch { get; init; }

    public string? Scope { get; init; }

    public int? Pid { get; init; }

    public int? Signal { get; init; }

    public int? GraceMs { get; init; }

    public int? Seq { get; init; }

    public bool? AllowRoot { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxOutput { get; init; }

    public int? Console { get; init; }

    public int? Cols { get; init; }

    public int? Rows { get; init; }

    public IReadOnlyList<int>? Keep { get; init; }
}

/// <summary>
/// A line of the agent: a reply (<c>{"id":N,"ok":true|false, ...}</c>) or an event
/// (<c>{"event":"...", ...}</c>). Every field but the first ones depends on the operation or the
/// event; a field the agent did not write keeps its default.
/// </summary>
internal sealed class AgentMessage
{
    public long? Id { get; set; }

    public bool? Ok { get; set; }

    public string? Error { get; set; }

    public string? Message { get; set; }

    public string? Event { get; set; }

    public int Version { get; set; }

    public long Uptime { get; set; }

    public long RssKb { get; set; }

    public int Launches { get; set; }

    public int Consoles { get; set; }

    public bool Cgroups { get; set; }

    public string? Launch { get; set; }

    public int Pid { get; set; }

    public int Uid { get; set; }

    public double Ms { get; set; }

    public string? Cgroup { get; set; }

    public bool Forced { get; set; }

    public int Signal { get; set; }

    public int Count { get; set; }

    public string? Target { get; set; }

    public int Code { get; set; }

    public bool TimedOut { get; set; }

    public bool Truncated { get; set; }

    public string? Output { get; set; }

    public int Console { get; set; }

    public int Cols { get; set; }

    public int Rows { get; set; }

    public int KeptCount { get; set; }

    public string? Kept { get; set; }

    public int Remaining { get; set; }

    public string? Prefix { get; set; }

    public string? Url { get; set; }

    public List<AgentProcessEntry>? Processes { get; set; }

    public List<AgentMountEntry>? Mounts { get; set; }

    public List<AgentWindowEntry>? Windows { get; set; }
}

/// <summary>An entry of the <c>list-processes</c> reply.</summary>
internal sealed class AgentProcessEntry
{
    public int Pid { get; set; }

    public int Ppid { get; set; }

    public int Pgid { get; set; }

    public int Uid { get; set; }

    public string? State { get; set; }

    public string? Prefix { get; set; }

    public string? Launch { get; set; }

    public string? Comm { get; set; }

    public string? Cmd { get; set; }

    public string? Cgroup { get; set; }
}

/// <summary>An entry of the <c>mounts</c> reply.</summary>
internal sealed class AgentMountEntry
{
    public string? Path { get; set; }

    public string? Type { get; set; }

    public string? Device { get; set; }

    public string? State { get; set; }

    public int StatfsErrno { get; set; }

    public int ReaddirErrno { get; set; }

    public int Entries { get; set; }

    public long Blocks { get; set; }

    public long BlockSize { get; set; }

    public long FreeBlocks { get; set; }

    public double Ms { get; set; }

    public string? Error { get; set; }
}

/// <summary>An entry of the <c>list-windows</c> reply (an agent with an X connection, work item 9).</summary>
internal sealed class AgentWindowEntry
{
    public string? Id { get; set; }

    public int Pid { get; set; }

    public string? Launch { get; set; }

    public string? Title { get; set; }
}

/// <summary>Source-generated JSON for the agent protocol (the library is trimmed in Kazo OS).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentMessage))]
internal sealed partial class AgentJsonContext : JsonSerializerContext
{
}

/// <summary>Serializes the requests, parses the lines and maps the replies of the agent protocol to the public types.</summary>
internal static class AgentProtocol
{
    /// <summary>The version of the protocol this client speaks.</summary>
    public const int Version = 1;

    /// <summary>The operation names.</summary>
    public static class Operations
    {
        public const string Ping = "ping";
        public const string ListProcesses = "list-processes";
        public const string Launch = "launch";
        public const string Kill = "kill";
        public const string ListWindows = "list-windows";
        public const string Mounts = "mounts";
        public const string ExecInPrefix = "exec-in-prefix";
        public const string OpenConsole = "open-console";
        public const string CloseConsole = "close-console";
        public const string ConsoleResize = "console-resize";
    }

    /// <summary>The event names.</summary>
    public static class Events
    {
        public const string Ready = "ready";
        public const string Exit = "exit";
        public const string OpenUrl = "open-url";
        public const string ConsoleClosed = "console-closed";
    }

    /// <summary>The scopes of an administrative kill.</summary>
    public static class Scopes
    {
        public const string Process = "pid";
        public const string Group = "group";
        public const string Prefix = "prefix";
        public const string Cgroup = "cgroup";
    }

    /// <summary>The request as one line of JSON (without the LF).</summary>
    public static string Serialize(AgentRequest request) => JsonSerializer.Serialize(request, AgentJsonContext.Default.AgentRequest);

    /// <summary>Parses a line of the agent.</summary>
    /// <exception cref="AgentProtocolException">The line is not a reply or an event of the protocol.</exception>
    public static AgentMessage Parse(string line)
    {
        AgentMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(line, AgentJsonContext.Default.AgentMessage);
        }
        catch (JsonException exception)
        {
            throw new AgentProtocolException(AgentErrorCodes.Protocol, $"The agent sent a line that is not of the protocol: {exception.Message}", exception);
        }

        if (message is null || (message.Event is null && message.Ok is null))
        {
            throw new AgentProtocolException(AgentErrorCodes.Protocol, "The agent sent a line that is neither a reply nor an event.");
        }

        return message;
    }

    /// <summary>The exception of a reply whose <c>ok</c> is false.</summary>
    public static AgentException ToException(AgentMessage reply) =>
        new(string.IsNullOrEmpty(reply.Error) ? AgentErrorCodes.Protocol : reply.Error, reply.Message ?? "The agent refused the request.");

    /// <summary>The launch identifier of a reply or an event.</summary>
    /// <exception cref="AgentProtocolException">The field is missing or malformed.</exception>
    public static LaunchId RequireLaunch(string? text) =>
        LaunchId.TryParse(text, out LaunchId id)
            ? id
            : throw new AgentProtocolException(AgentErrorCodes.Protocol, $"The agent sent the launch identifier '{text}', which is not <prefix>/<sequence>.");

    public static LaunchId? OptionalLaunch(string? text) => LaunchId.TryParse(text, out LaunchId id) ? id : null;

    public static AgentStatus ToStatus(AgentMessage reply) =>
        new(reply.Version, TimeSpan.FromSeconds(reply.Uptime), reply.RssKb, reply.Launches, reply.Consoles, reply.Cgroups);

    public static ProcessExit ToExit(AgentMessage message) => new(message.Code, message.Signal);

    public static IReadOnlyList<ProcessInfo> ToProcesses(AgentMessage reply) =>
        (reply.Processes ?? []).Select(p => new ProcessInfo(p.Pid, p.Ppid, p.Pgid, p.Uid, p.State ?? string.Empty, p.Prefix,
            OptionalLaunch(p.Launch), p.Comm ?? string.Empty, p.Cmd ?? string.Empty, p.Cgroup ?? string.Empty)).ToList();

    public static IReadOnlyList<MountInfo> ToMounts(AgentMessage reply) => (reply.Mounts ?? []).Select(ToMount).ToList();

    public static IReadOnlyList<WindowInfo> ToWindows(AgentMessage reply) =>
        (reply.Windows ?? []).Select(w => new WindowInfo(w.Id ?? string.Empty, w.Pid, OptionalLaunch(w.Launch), w.Title ?? string.Empty)).ToList();

    public static ExecResult ToExecResult(AgentMessage reply) =>
        new(ToExit(reply), reply.TimedOut, reply.Truncated, reply.Output ?? string.Empty, TimeSpan.FromMilliseconds(reply.Ms));

    /// <summary>The event of a line whose <c>event</c> is set; a fault for a name this client does not know.</summary>
    public static AgentEvent ToEvent(AgentMessage message) => message.Event switch
    {
        Events.Ready => new AgentReadyEvent(message.Version, message.Pid, message.Cgroups),
        Events.Exit => new LaunchExitedEvent(RequireLaunch(message.Launch), ToExit(message), message.Remaining),
        Events.ConsoleClosed => new ConsoleClosedEvent(RequireLaunch(message.Launch), message.Console, ToExit(message), message.Remaining),
        Events.OpenUrl => new OpenUrlEvent(message.Prefix, message.Uid, message.Pid, message.Url ?? string.Empty),
        _ => new AgentFaultEvent(AgentErrorCodes.Protocol, $"The agent sent an unknown event '{message.Event}'."),
    };

    private static MountInfo ToMount(AgentMountEntry mount)
    {
        MountState state = mount.State switch
        {
            "ok" => MountState.Ok,
            "hung" => MountState.Hung,
            _ => MountState.Error,
        };
        int errorNumber = mount.StatfsErrno != 0 ? mount.StatfsErrno : mount.ReaddirErrno;
        return new MountInfo(mount.Path ?? string.Empty, mount.Type ?? string.Empty, mount.Device ?? string.Empty, state, mount.Entries,
            mount.Blocks * mount.BlockSize, mount.FreeBlocks * mount.BlockSize, errorNumber, mount.Error ?? string.Empty);
    }
}
