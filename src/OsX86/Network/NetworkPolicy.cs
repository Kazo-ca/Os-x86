namespace OsX86.Network;

/// <summary>The decision on a flow, and why.</summary>
/// <param name="Allowed">Whether it may go to the relay.</param>
/// <param name="Reason">One of <see cref="NetworkJournalReasons"/>: <c>rule</c>, <c>prompt</c> or <c>no-prompt</c>.</param>
/// <param name="RuleId">The rule that decided (a written or a session rule), if any.</param>
public readonly record struct FlowDecision(bool Allowed, string Reason, int? RuleId);

/// <summary>
/// Decides a flow of one user (decisions D14, D20, D21, 174 A): the rules first; when none decides,
/// or one says "ask", the permission window. Connections to the same destination of the same
/// identity that wait together share one window, which belongs to none of them: a connection that
/// goes (its VM stopped) leaves the question open for the others, and the shared question is
/// forgotten as soon as it is answered, so a later connection never reuses an answer given for
/// "this connection". An answer for the session or for always becomes a rule (for this host and
/// port, or for any destination).
/// </summary>
public sealed class NetworkPolicy
{
    private readonly NetworkRuleBook _rules;
    private readonly INetworkPermissionPrompt _prompt;
    private readonly CancellationToken _lifetime;
    private readonly Lock _lock = new();
    private readonly Dictionary<(FlowIdentity, string, int), Task<FlowDecision>> _asking = [];

    /// <summary>Creates the policy of a user.</summary>
    /// <param name="rules">The user's rules.</param>
    /// <param name="prompt">The permission window.</param>
    /// <param name="lifetime">Ends the questions still open (the network goes away).</param>
    public NetworkPolicy(NetworkRuleBook rules, INetworkPermissionPrompt prompt, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(prompt);
        _rules = rules;
        _prompt = prompt;
        _lifetime = lifetime;
    }

    /// <summary>Decides a connection of <paramref name="identity"/> (in the VM <paramref name="vmName"/>) to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public async Task<FlowDecision> DecideAsync(FlowIdentity identity, string vmName, string host, int port, CancellationToken cancellationToken)
    {
        RuleVerdict verdict = await _rules.DecideAsync(identity, host, port, cancellationToken).ConfigureAwait(false);
        if (verdict.Action is NetworkRuleAction.Allow or NetworkRuleAction.Deny)
        {
            return new FlowDecision(verdict.Action == NetworkRuleAction.Allow, NetworkJournalReasons.Rule, verdict.Rule!.Id);
        }

        Task<FlowDecision> asking;
        lock (_lock)
        {
            if (!_asking.TryGetValue((identity, host, port), out asking!))
            {
                asking = AskAsync(identity, vmName, host, port);
                _asking[(identity, host, port)] = asking;
            }
        }

        return await asking.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FlowDecision> AskAsync(FlowIdentity identity, string vmName, string host, int port)
    {
        try
        {
            await Task.Yield(); // registered as "asking" before the window opens
            return await AskNowAsync(identity, vmName, host, port).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _asking.Remove((identity, host, port)); // answered or failed: the next connection asks again or meets the rule
            }
        }
    }

    private async Task<FlowDecision> AskNowAsync(FlowIdentity identity, string vmName, string host, int port)
    {
        CancellationToken cancellationToken = _lifetime;
        NetworkPermissionAnswer? answer = await _prompt.AskAsync(new NetworkPermissionRequest(_rules.UserId, identity, vmName, host, port), cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return new FlowDecision(false, NetworkJournalReasons.NoPrompt, null);
        }

        int? ruleId = null;
        if (answer.Duration != PermissionDuration.Once)
        {
            NetworkDestination destination = answer.AnyDestination ? NetworkDestination.Any : new NetworkDestination(host.ToLowerInvariant(), port);
            NetworkRule rule = await _rules.AddAsync(identity, destination, answer.Allow ? NetworkRuleAction.Allow : NetworkRuleAction.Deny, answer.Duration == PermissionDuration.Session, cancellationToken).ConfigureAwait(false);
            ruleId = rule.Id;
        }

        return new FlowDecision(answer.Allow, NetworkJournalReasons.Prompt, ruleId);
    }
}
