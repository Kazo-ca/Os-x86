using System.Text.Json;
using System.Text.Json.Serialization;
using OsX86.Interface;
using OsX86.Prefixes;
using OsX86.Vm;

namespace OsX86.Network;

/// <summary>What happened to a flow, as the journal says.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkJournalEvent>))]
public enum NetworkJournalEvent
{
    /// <summary>The flow was allowed and the relay opened it.</summary>
    Opened,

    /// <summary>An opened flow ended (its bytes and duration are given).</summary>
    Closed,

    /// <summary>The flow was refused before reaching the relay, or by it.</summary>
    Refused,
}

/// <summary>Why a flow was refused or closed (the <c>reason</c> of a journal entry).</summary>
public static class NetworkJournalReasons
{
    /// <summary>A rule allowed it.</summary>
    public const string Rule = "rule";

    /// <summary>The user answered the permission window.</summary>
    public const string Prompt = "prompt";

    /// <summary>No rule decided and no permission window could be shown (ADR 0246 of Kazo not in a released version yet).</summary>
    public const string NoPrompt = "no-prompt";

    /// <summary>The port is not one the limits allow.</summary>
    public const string Port = "port";

    /// <summary>The identity already has its flows at once.</summary>
    public const string FlowsPerIdentity = "flows-identity";

    /// <summary>The user already has its flows at once.</summary>
    public const string FlowsPerUser = "flows-user";

    /// <summary>The relay refused or could not reach the destination.</summary>
    public const string Relay = "relay";

    /// <summary>UDP (DNS included) never leaves the private network (174 A).</summary>
    public const string Udp = "udp";

    /// <summary>The flow was idle longer than the limit.</summary>
    public const string Idle = "idle";

    /// <summary>Both sides finished.</summary>
    public const string Ended = "ended";

    /// <summary>The guest reset the connection.</summary>
    public const string Reset = "reset";

    /// <summary>The VM stopped.</summary>
    public const string VmStopped = "vm-stopped";
}

/// <summary>
/// One line of a user's network journal (decision 174 A; ADR 0255 §3: per identity): when, who
/// (the VM and the prefix), where, what happened and why.
/// </summary>
public sealed record NetworkJournalEntry
{
    /// <summary>When.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The VM.</summary>
    public required Guid Vm { get; init; }

    /// <summary>The prefix, or <see langword="null"/> for the VM itself.</summary>
    public PrefixId? Prefix { get; init; }

    /// <summary>The destination host (an IPv4 address in milestone 0).</summary>
    public required string Host { get; init; }

    /// <summary>The destination port.</summary>
    public required int Port { get; init; }

    /// <summary>What happened.</summary>
    public required NetworkJournalEvent Event { get; init; }

    /// <summary>Why (one of <see cref="NetworkJournalReasons"/>).</summary>
    public required string Reason { get; init; }

    /// <summary>The rule that decided, when one did.</summary>
    public int? Rule { get; init; }

    /// <summary>Bytes the guest sent (a closed flow).</summary>
    public long? BytesOut { get; init; }

    /// <summary>Bytes the guest received (a closed flow).</summary>
    public long? BytesIn { get; init; }

    /// <summary>How long the flow lasted, in milliseconds (a closed flow).</summary>
    public long? DurationMs { get; init; }

    /// <summary>A detail (the relay's code, the protocol of a refused datagram).</summary>
    public string? Detail { get; init; }

    /// <summary>The identity of the entry.</summary>
    [JsonIgnore]
    public FlowIdentity Identity => new(Vm, Prefix);
}

/// <summary>
/// The rules a user keeps (plan §13, versioned). Format 1:
/// <code>
/// {"format": 1, "user": "0000…0001", "nextId": 3,
///  "rules": [{"id": 1, "vmId": "…", "prefix": "p1", "destination": {"host": "203.0.113.9", "port": 80},
///             "action": "Allow", "created": "2026-11-16T10:00:00+00:00"}]}
/// </code>
/// An unknown format is refused and nothing is written (169).
/// </summary>
public sealed class NetworkRulesDocument
{
    /// <summary>The only format this version reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The largest text accepted, in bytes.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The format number.</summary>
    public int Format { get; init; } = CurrentFormat;

    /// <summary>The user the rules belong to.</summary>
    public required Guid User { get; init; }

    /// <summary>The number the next rule gets.</summary>
    public int NextId { get; init; } = 1;

    /// <summary>The rules, in creation order (never the session ones).</summary>
    public IReadOnlyList<NetworkRule> Rules { get; init; } = [];

    /// <summary>The JSON text, as stored.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, NetworkJsonContext.Indented.NetworkRulesDocument);

    /// <summary>Reads a rules text; refuses an unknown format, another user's document or an invalid rule.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.UnsupportedFormat"/> or <see cref="X86ErrorCodes.InvalidProfile"/>.</exception>
    public static NetworkRulesDocument Parse(string json, Guid expectedUser)
    {
        NetworkRulesDocument document = NetworkDocuments.Parse(json, "network rules", MaxBytes, CurrentFormat, NetworkJsonContext.Default.NetworkRulesDocument);
        if (document.User != expectedUser || document.Rules is null || document.Rules.Any(static rule => rule is null))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, "The network rules are empty, invalid or belong to another user.");
        }

        var ids = new HashSet<int>();
        foreach (NetworkRule rule in document.Rules)
        {
            if (rule.Id < 1 || rule.Id >= document.NextId || !ids.Add(rule.Id) || rule.VmId == Guid.Empty || rule.Destination is null || rule.Session
                || !Enum.IsDefined(rule.Action) || (rule.Destination.Host is { } host && !NetworkDestination.IsValidHost(host))
                || rule.Destination.Port is < 1 or > 65535)
            {
                throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The network rules are invalid: rule {rule.Id} is malformed or duplicated.");
            }
        }

        return document;
    }
}

/// <summary>
/// A user's network journal (plan §13, versioned; ADR 0255 §3 keeps 7 days and 1000 entries at the
/// relay, the same bounds here). Format 1:
/// <code>
/// {"format": 1, "user": "0000…0001", "entries": [{"time": "…", "vm": "…", "prefix": "p1", "host": "203.0.113.9",
///   "port": 80, "event": "Opened", "reason": "prompt"}]}
/// </code>
/// </summary>
public sealed class NetworkJournalDocument
{
    /// <summary>The only format this version reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The largest text accepted, in bytes.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The most entries kept (the oldest go first).</summary>
    public const int MaxEntries = 1000;

    /// <summary>How long an entry is kept.</summary>
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(7);

    /// <summary>The format number.</summary>
    public int Format { get; init; } = CurrentFormat;

    /// <summary>The user the journal belongs to.</summary>
    public required Guid User { get; init; }

    /// <summary>The entries, oldest first.</summary>
    public IReadOnlyList<NetworkJournalEntry> Entries { get; init; } = [];

    /// <summary>The JSON text, as stored (compact: it is read by Os-x86, not by hand).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, NetworkJsonContext.Default.NetworkJournalDocument);

    /// <summary>Reads a journal text; refuses an unknown format or another user's document.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.UnsupportedFormat"/> or <see cref="X86ErrorCodes.InvalidProfile"/>.</exception>
    public static NetworkJournalDocument Parse(string json, Guid expectedUser)
    {
        NetworkJournalDocument document = NetworkDocuments.Parse(json, "network journal", MaxBytes, CurrentFormat, NetworkJsonContext.Default.NetworkJournalDocument);
        if (document.User != expectedUser || document.Entries is null || document.Entries.Any(static entry => entry is null || entry.Host is null || entry.Reason is null))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, "The network journal is empty, invalid or belongs to another user.");
        }

        return document;
    }

    /// <summary>The entries with <paramref name="added"/> appended, older than the retention or beyond the bound removed.</summary>
    public static IReadOnlyList<NetworkJournalEntry> Append(IReadOnlyList<NetworkJournalEntry> entries, NetworkJournalEntry added, DateTimeOffset now)
    {
        DateTimeOffset oldest = now - Retention;
        List<NetworkJournalEntry> kept = [.. entries.Where(entry => entry.Time >= oldest), added];
        return kept.Count > MaxEntries ? kept[^MaxEntries..] : kept;
    }
}

/// <summary>Where a user's network documents are kept (one text per user and kind, like the VM registry).</summary>
public interface INetworkStore
{
    /// <summary>The text of a document of a user, or <see langword="null"/> when there is none.</summary>
    ValueTask<string?> ReadAsync(Guid userId, NetworkDocumentKind kind, CancellationToken cancellationToken);

    /// <summary>Replaces the text of a document of a user.</summary>
    ValueTask WriteAsync(Guid userId, NetworkDocumentKind kind, string json, CancellationToken cancellationToken);
}

/// <summary>The network documents of a user.</summary>
public enum NetworkDocumentKind
{
    /// <summary>The rules (<see cref="NetworkRulesDocument"/>).</summary>
    Rules,

    /// <summary>The journal (<see cref="NetworkJournalDocument"/>).</summary>
    Journal,
}

/// <summary>A network store in memory: tests and hosts without storage.</summary>
public sealed class InMemoryNetworkStore : INetworkStore
{
    private readonly Dictionary<(Guid, NetworkDocumentKind), string> _documents = [];
    private readonly Lock _lock = new();

    /// <summary>How many writes happened.</summary>
    public int Writes { get; private set; }

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(Guid userId, NetworkDocumentKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return ValueTask.FromResult(_documents.GetValueOrDefault((userId, kind)));
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(Guid userId, NetworkDocumentKind kind, string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _documents[(userId, kind)] = json;
            Writes++;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>The common reading of the network documents.</summary>
internal static class NetworkDocuments
{
    public static T Parse<T>(string json, string what, int maxBytes, int format, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The {what} are more than the {maxBytes} bytes accepted.");
        }

        int found = VersionedDocument.PeekFormat(json, what);
        if (found != format)
        {
            throw new X86Exception(X86ErrorCodes.UnsupportedFormat, $"The {what} have format {found}; this version of Os-x86 reads format {format} only. They are refused and nothing was written.");
        }

        try
        {
            return JsonSerializer.Deserialize(json, type) ?? throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The {what} are empty.");
        }
        catch (JsonException exception)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The {what} are invalid: {exception.Message}", exception);
        }
    }
}

/// <summary>The serializer context of the network documents (source-generated: the library is trimmed).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(NetworkRulesDocument))]
[JsonSerializable(typeof(NetworkJournalDocument))]
internal sealed partial class NetworkJsonContext : JsonSerializerContext
{
    /// <summary>The same context with indented output (the rules a user may read).</summary>
    public static NetworkJsonContext Indented => s_indented ??= new(new JsonSerializerOptions(Default!.Options) { WriteIndented = true });

    private static NetworkJsonContext? s_indented;
}
