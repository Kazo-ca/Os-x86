using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using OsX86.Prefixes;

namespace OsX86.Network;

/// <summary>What a rule does with a connection it matches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkRuleAction>))]
public enum NetworkRuleAction
{
    /// <summary>The connection goes through the relay (within the limits: "allow" never lifts them, D14).</summary>
    Allow,

    /// <summary>The connection is refused.</summary>
    Deny,

    /// <summary>The permission window is opened every time (nothing is remembered).</summary>
    Ask,
}

/// <summary>
/// Where a rule applies: a host (an IPv4 address, or a name), a port, both, or anything. Written
/// <c>any</c>, <c>host</c>, <c>host:port</c> or <c>*:port</c>.
/// </summary>
/// <param name="Host">The host, lowercase; <see langword="null"/> for any host.</param>
/// <param name="Port">The TCP port; <see langword="null"/> for any port.</param>
public sealed record NetworkDestination(string? Host, int? Port)
{
    /// <summary>Any host, any port.</summary>
    public static NetworkDestination Any { get; } = new(null, null);

    /// <summary>How specific it is: a host counts 2, a port 1 (the most specific matching rule wins).</summary>
    [JsonIgnore]
    public int Specificity => (Host is null ? 0 : 2) + (Port is null ? 0 : 1);

    /// <summary>Whether a connection to <paramref name="host"/>:<paramref name="port"/> is covered.</summary>
    public bool Matches(string host, int port) =>
        (Host is null || string.Equals(Host, host, StringComparison.OrdinalIgnoreCase)) && (Port is null || Port == port);

    /// <summary>Reads <c>any</c>, <c>host</c>, <c>host:port</c> or <c>*:port</c>; false with the reason otherwise.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out NetworkDestination? destination, [NotNullWhen(false)] out string? error)
    {
        destination = null;
        error = null;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "a destination is any, a host, host:port or *:port";
            return false;
        }

        if (string.Equals(value, "any", StringComparison.OrdinalIgnoreCase) || value == "*")
        {
            destination = Any;
            return true;
        }

        int colon = value.LastIndexOf(':');
        string host = colon < 0 ? value : value[..colon];
        int? port = null;
        if (colon >= 0)
        {
            if (!int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed is < 1 or > 65535)
            {
                error = $"'{value[(colon + 1)..]}' is not a TCP port (1 to 65535)";
                return false;
            }

            port = parsed;
        }

        if (host == "*")
        {
            destination = new NetworkDestination(null, port);
            return true;
        }

        if (!IsValidHost(host))
        {
            error = $"'{host}' is not a host (an IPv4 address or a name of letters, digits, dots and dashes)";
            return false;
        }

        destination = new NetworkDestination(host.ToLowerInvariant(), port);
        return true;
    }

    /// <summary>Whether a host name or address is acceptable in a rule (no IPv6: 174 A blocks it).</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host[0] is '.' or '-' || host[^1] is '.' or '-')
        {
            return false;
        }

        return host.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    }

    /// <inheritdoc />
    public override string ToString() => (Host, Port) switch
    {
        (null, null) => "any",
        (null, int port) => $"*:{port.ToString(CultureInfo.InvariantCulture)}",
        (string host, null) => host,
        (string host, int port) => $"{host}:{port.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>
/// A rule of a user (D61.1: network decisions are per user), for one identity: a prefix of a VM,
/// or the VM itself. Rules remembered "for the session" are not stored.
/// </summary>
/// <param name="Id">Its number among the user's rules (what <c>x86 rules remove</c> takes).</param>
/// <param name="VmId">The VM.</param>
/// <param name="Prefix">The prefix, or <see langword="null"/> for the VM itself.</param>
/// <param name="Destination">Where it applies.</param>
/// <param name="Action">What it does.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Session">Whether it lasts only until the end of the session (never written).</param>
public sealed record NetworkRule(int Id, Guid VmId, PrefixId? Prefix, NetworkDestination Destination, NetworkRuleAction Action, DateTimeOffset Created, bool Session = false)
{
    /// <summary>The identity the rule is for.</summary>
    [JsonIgnore]
    public FlowIdentity Identity => new(VmId, Prefix);
}

/// <summary>What the rules say of a connection.</summary>
/// <param name="Action">The action of the rule that wins, or <see langword="null"/> when none matches (undecided).</param>
/// <param name="Rule">That rule.</param>
public readonly record struct RuleVerdict(NetworkRuleAction? Action, NetworkRule? Rule)
{
    /// <summary>No rule matches.</summary>
    public static RuleVerdict Undecided { get; } = new(null, null);
}

/// <summary>Picks the rule that decides a connection.</summary>
public static class NetworkRuleMatcher
{
    /// <summary>
    /// The rule of <paramref name="identity"/> that decides a connection to
    /// <paramref name="host"/>:<paramref name="port"/>: the most specific matching rule (host and
    /// port, then host, then port, then any); between equally specific rules, a rule of the session
    /// wins over a written one (the answer given for the session holds while the written rule stays
    /// for later), then deny wins over ask, and ask over allow.
    /// </summary>
    public static RuleVerdict Decide(IEnumerable<NetworkRule> rules, FlowIdentity identity, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(rules);
        NetworkRule? best = null;
        foreach (NetworkRule rule in rules)
        {
            if (rule.Identity != identity || !rule.Destination.Matches(host, port))
            {
                continue;
            }

            if (best is null || Outranks(rule, best))
            {
                best = rule;
            }
        }

        return best is null ? RuleVerdict.Undecided : new RuleVerdict(best.Action, best);
    }

    private static bool Outranks(NetworkRule candidate, NetworkRule current)
    {
        int bySpecificity = candidate.Destination.Specificity.CompareTo(current.Destination.Specificity);
        if (bySpecificity != 0)
        {
            return bySpecificity > 0;
        }

        if (candidate.Session != current.Session)
        {
            return candidate.Session;
        }

        return Strength(candidate.Action) > Strength(current.Action);
    }

    private static int Strength(NetworkRuleAction action) => action switch
    {
        NetworkRuleAction.Deny => 2,
        NetworkRuleAction.Ask => 1,
        _ => 0,
    };
}

/// <summary>
/// The limits Os-x86 holds for every flow (trial E6, decision 174 A; until the relay of Kazo holds
/// them per identity, ADR 0255 §2): flows at once per identity and per user, the TCP ports, the
/// idle time after which a flow is cut. "Allow" never lifts them (D14).
/// </summary>
public sealed record NetworkLimits
{
    /// <summary>Flows open at once for one identity (E6: 4).</summary>
    public int MaxFlowsPerIdentity { get; init; } = 4;

    /// <summary>Flows open at once for all the identities of a user (the device ceiling of the relay, 4).</summary>
    public int MaxFlowsPerUser { get; init; } = 4;

    /// <summary>The TCP ports a flow may reach (E6 and the relay of Kazo: 80 and 443).</summary>
    public IReadOnlySet<int> AllowedPorts { get; init; } = new HashSet<int> { 80, 443 };

    /// <summary>A flow without traffic for this long is cut (E6 and the relay of Kazo: 300 s).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// How long a connection may wait for its decision (the window unanswered, a relay that does not
    /// answer) before it is refused: the guest's own connect gives up after about two minutes.
    /// </summary>
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Throws when a limit makes no sense.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFlowsPerIdentity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFlowsPerUser, 1);
        ArgumentNullException.ThrowIfNull(AllowedPorts);
        if (AllowedPorts.Any(static port => port is < 1 or > 65535))
        {
            throw new ArgumentException("A port is 1 to 65535.", nameof(AllowedPorts));
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(IdleTimeout), IdleTimeout, "The idle time must be positive.");
        }

        if (DecisionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DecisionTimeout), DecisionTimeout, "The decision time must be positive.");
        }
    }
}
