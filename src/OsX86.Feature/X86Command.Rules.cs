using System.Globalization;
using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Network;
using OsX86.Prefixes;
using OsX86.Vm;

namespace OsX86.Feature;

/// <content>
/// Work item 12: <c>x86 rules</c>, the command-line face of the page of the rules (decisions 174 A,
/// D61.1): the user's network rules per prefix (or per VM) and per destination, and the journal.
/// A rule typed here is the user's own command, never an answer to a window a guest could imitate (D49).
/// </content>
public sealed partial class X86Command
{
    private const int DefaultLogLines = 20;
    private static readonly string[] RuleOptions = ["vm", "prefix"];
    private static readonly string[] LogOptions = ["vm", "prefix", "last"];
    private static readonly string[] MachineFlag = ["machine"];

    private async ValueTask<int> RulesAsync(TerminalExecutionContext context, Guid user, List<string> args, CancellationToken cancellationToken)
    {
        string verb = args.Count > 0 ? args[0] : string.Empty;
        (string[] options, string[] flags, int positional) = verb switch
        {
            "list" => (RuleOptions, MachineFlag, 0),
            "allow" or "deny" or "ask" => (RuleOptions, MachineFlag, 1),
            "remove" => (NoOptions, NoOptions, 1),
            "log" => (LogOptions, MachineFlag, 0),
            _ => (NoOptions, NoOptions, -1),
        };
        if (positional < 0)
        {
            return await UsageErrorAsync(context, verb.Length == 0 ? "rules needs a subcommand" : $"unknown command: rules {verb}", cancellationToken).ConfigureAwait(false);
        }

        CommandArguments? parsed = CommandArguments.Parse(args.Skip(1), options, flags, out string? error);
        if (parsed is null || parsed.Positional.Count != positional)
        {
            return await UsageErrorAsync(context, error ?? $"rules {verb} takes {positional} argument(s)", cancellationToken).ConfigureAwait(false);
        }

        if (parsed.Has("machine") && parsed.Value("prefix") is not null)
        {
            return await UsageErrorAsync(context, "--machine and --prefix name two different identities: give one", cancellationToken).ConfigureAwait(false);
        }

        UserNetwork network = (Manager.Network ?? throw new X86Exception(X86ErrorCodes.NetworkUnavailable, "This host has no private network for the virtual machines.")).For(user);
        return verb switch
        {
            "list" => await ListRulesAsync(context, user, network, parsed, cancellationToken).ConfigureAwait(false),
            "remove" => await RemoveRuleAsync(context, network, parsed.Positional[0], cancellationToken).ConfigureAwait(false),
            "log" => await ShowJournalAsync(context, user, network, parsed, cancellationToken).ConfigureAwait(false),
            _ => await AddRuleAsync(context, user, network, verb, parsed, cancellationToken).ConfigureAwait(false),
        };
    }

    private async ValueTask<int> AddRuleAsync(TerminalExecutionContext context, Guid user, UserNetwork network, string verb, CommandArguments parsed, CancellationToken cancellationToken)
    {
        if (!NetworkDestination.TryParse(parsed.Positional[0], out NetworkDestination? destination, out string? error))
        {
            return await UsageErrorAsync(context, error, cancellationToken).ConfigureAwait(false);
        }

        VmRecord? vm = await PrefixVmAsync(user, parsed.Value("vm"), cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            return await UsageErrorAsync(context, "you have several virtual machines: name one with --vm", cancellationToken).ConfigureAwait(false);
        }

        FlowIdentity identity = IdentityOf(vm, parsed);
        NetworkRuleAction action = verb switch { "allow" => NetworkRuleAction.Allow, "deny" => NetworkRuleAction.Deny, _ => NetworkRuleAction.Ask };
        NetworkRule rule = await network.Rules.AddAsync(identity, destination, action, session: false, cancellationToken).ConfigureAwait(false);
        await context.StandardOutput.WriteLineAsync($"Rule {rule.Id}: {Describe(action)} {destination} for {WhoOf(vm.Name, identity)}.".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (action == NetworkRuleAction.Allow)
        {
            NetworkLimits limits = Manager.Network!.Limits;
            string ports = string.Join(", ", limits.AllowedPorts.Order());
            await context.StandardOutput.WriteLineAsync(
                string.Create(CultureInfo.InvariantCulture, $"The limits still hold: ports {ports}, {limits.MaxFlowsPerIdentity} connections at once, cut after {limits.IdleTimeout.TotalSeconds:0} s idle.").AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }

        return X86ExitCodes.Ok;
    }

    private static async ValueTask<int> RemoveRuleAsync(TerminalExecutionContext context, UserNetwork network, string text, CancellationToken cancellationToken)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id) || id == 0)
        {
            return await UsageErrorAsync(context, $"'{text}' is not a rule number (see `x86 rules list`)", cancellationToken).ConfigureAwait(false);
        }

        if (!await network.Rules.RemoveAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new X86Exception(X86ErrorCodes.RuleNotFound, $"You have no network rule {id} (see `x86 rules list`).");
        }

        await context.StandardOutput.WriteLineAsync($"Removed rule {id}.".AsMemory(), cancellationToken).ConfigureAwait(false);
        return X86ExitCodes.Ok;
    }

    private async ValueTask<int> ListRulesAsync(TerminalExecutionContext context, Guid user, UserNetwork network, CommandArguments parsed, CancellationToken cancellationToken)
    {
        Func<FlowIdentity, bool> keep = await FilterAsync(user, parsed, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<NetworkRule> rules = await network.Rules.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, string> names = await VmNamesAsync(user, cancellationToken).ConfigureAwait(false);
        await WriteLinesAsync(context.StandardOutput, FormatRules([.. rules.Where(rule => keep(rule.Identity))], names), cancellationToken).ConfigureAwait(false);
        return X86ExitCodes.Ok;
    }

    private async ValueTask<int> ShowJournalAsync(TerminalExecutionContext context, Guid user, UserNetwork network, CommandArguments parsed, CancellationToken cancellationToken)
    {
        int last = DefaultLogLines;
        if (parsed.Value("last") is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < 1))
        {
            return await UsageErrorAsync(context, $"--last takes a number of entries, not '{text}'", cancellationToken).ConfigureAwait(false);
        }

        Func<FlowIdentity, bool> keep = await FilterAsync(user, parsed, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<NetworkJournalEntry> entries = await network.Journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, string> names = await VmNamesAsync(user, cancellationToken).ConfigureAwait(false);
        List<NetworkJournalEntry> kept = [.. entries.Where(entry => keep(entry.Identity))];
        await WriteLinesAsync(context.StandardOutput, FormatJournal(kept.Count > last ? kept[^last..] : kept, names), cancellationToken).ConfigureAwait(false);
        return X86ExitCodes.Ok;
    }

    /// <summary>What <c>--vm</c>, <c>--prefix</c> and <c>--machine</c> keep of a list (everything without them).</summary>
    private async ValueTask<Func<FlowIdentity, bool>> FilterAsync(Guid user, CommandArguments parsed, CancellationToken cancellationToken)
    {
        if (parsed.Value("vm") is null && parsed.Value("prefix") is null && !parsed.Has("machine"))
        {
            return static _ => true;
        }

        VmRecord vm = await PrefixVmAsync(user, parsed.Value("vm"), cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, "You have several virtual machines: name one with --vm.");
        if (parsed.Value("prefix") is null && !parsed.Has("machine"))
        {
            return identity => identity.VmId == vm.Id;
        }

        FlowIdentity wanted = IdentityOf(vm, parsed);
        return identity => identity == wanted;
    }

    /// <summary>The identity a rule command names: <c>--machine</c> the VM itself, <c>--prefix</c> that prefix, else the VM's default prefix.</summary>
    private static FlowIdentity IdentityOf(VmRecord vm, CommandArguments parsed)
    {
        if (parsed.Has("machine"))
        {
            return new FlowIdentity(vm.Id, null);
        }

        if (parsed.Value("prefix") is not { } named)
        {
            return new FlowIdentity(vm.Id, vm.DefaultPrefix);
        }

        if (!PrefixId.TryParse(named, out PrefixId prefix))
        {
            throw new X86Exception(X86ErrorCodes.InvalidName, $"'{named}' is not a prefix name (lowercase letters, digits and dashes, 1 to 16 characters).");
        }

        return vm.KnowsPrefix(prefix)
            ? new FlowIdentity(vm.Id, prefix)
            : throw new X86Exception(X86ErrorCodes.PrefixNotFound, $"The virtual machine {vm.Name} has no prefix named '{prefix}'.");
    }

    private async ValueTask<IReadOnlyDictionary<Guid, string>> VmNamesAsync(Guid user, CancellationToken cancellationToken) =>
        (await Manager.Registry.ListAsync(user, cancellationToken).ConfigureAwait(false)).ToDictionary(vm => vm.Id, vm => vm.Name);

    private static string Describe(NetworkRuleAction action) => action switch
    {
        NetworkRuleAction.Allow => "allow",
        NetworkRuleAction.Deny => "deny",
        _ => "ask every time for",
    };

    private static string WhoOf(string vmName, FlowIdentity identity) =>
        identity.Prefix is { } prefix ? $"the prefix {prefix} of {vmName}" : $"the virtual machine {vmName} itself";

    private static string NameOf(IReadOnlyDictionary<Guid, string> names, Guid vmId) =>
        names.TryGetValue(vmId, out string? name) ? name : vmId.ToString("N")[..8];

    internal static IEnumerable<string> FormatRules(IReadOnlyList<NetworkRule> rules, IReadOnlyDictionary<Guid, string> names)
    {
        if (rules.Count == 0)
        {
            yield return "No network rule: the first connection to a destination asks (or is refused when no window can be shown).";
            yield break;
        }

        yield return string.Format(CultureInfo.InvariantCulture, "{0,-5}{1,-16}{2,-18}{3,-28}{4,-8}{5}", "ID", "VM", "PREFIX", "DESTINATION", "ACTION", "KEPT");
        foreach (NetworkRule rule in rules)
        {
            yield return string.Format(
                CultureInfo.InvariantCulture,
                "{0,-5}{1,-16}{2,-18}{3,-28}{4,-8}{5}",
                rule.Id,
                NameOf(names, rule.VmId),
                rule.Prefix?.Value ?? "(machine)",
                rule.Destination,
                rule.Action.ToString().ToLowerInvariant(),
                rule.Session ? "session" : "always");
        }
    }

    internal static IEnumerable<string> FormatJournal(IReadOnlyList<NetworkJournalEntry> entries, IReadOnlyDictionary<Guid, string> names)
    {
        if (entries.Count == 0)
        {
            yield return "The network journal is empty.";
            yield break;
        }

        yield return string.Format(CultureInfo.InvariantCulture, "{0,-21}{1,-16}{2,-18}{3,-24}{4,-9}{5,-16}{6,-6}{7}", "TIME (UTC)", "VM", "PREFIX", "DESTINATION", "EVENT", "REASON", "RULE", "DETAIL");
        foreach (NetworkJournalEntry entry in entries)
        {
            string detail = entry.Event == NetworkJournalEvent.Closed
                ? string.Create(CultureInfo.InvariantCulture, $"out {entry.BytesOut ?? 0} B, in {entry.BytesIn ?? 0} B, {entry.DurationMs ?? 0} ms")
                : entry.Detail ?? string.Empty;
            yield return string.Format(
                CultureInfo.InvariantCulture,
                "{0,-21:yyyy-MM-dd HH:mm:ss}{1,-16}{2,-18}{3,-24}{4,-9}{5,-16}{6,-6}{7}",
                entry.Time.ToUniversalTime(),
                NameOf(names, entry.Vm),
                entry.Prefix?.Value ?? "(machine)",
                $"{entry.Host}:{entry.Port.ToString(CultureInfo.InvariantCulture)}",
                entry.Event.ToString().ToLowerInvariant(),
                entry.Reason,
                entry.Rule?.ToString(CultureInfo.InvariantCulture) ?? "-",
                detail).TrimEnd();
        }
    }
}
