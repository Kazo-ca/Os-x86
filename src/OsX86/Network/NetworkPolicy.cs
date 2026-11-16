namespace OsX86.Network;

/// <summary>The decision on a flow, and why.</summary>
/// <param name="Allowed">Whether it may go to the relay.</param>
/// <param name="Reason">One of <see cref="NetworkJournalReasons"/>: <c>rule</c>, <c>prompt</c> or <c>no-prompt</c>.</param>
/// <param name="RuleId">The rule that decided (a written or a session rule), if any.</param>
public readonly record struct FlowDecision(bool Allowed, string Reason, int? RuleId);

/// <summary>
/// Decides a flow of one user (decisions D14, D20, D21, 174 A): the rules first; when none decides,
/// or one says "ask", the permission window. Connections to the same destination of the same
/// identity that wait together share one window. An answer for the session or for always becomes
/// a rule (for this host and port, or for any destination).
/// </summary>
public sealed class NetworkPolicy
{
    private readonly NetworkRuleBook _rules;
    private readonly INetworkPermissionPrompt _prompt;
    private readonly Lock _lock = new();
    private readonly Dictionary<(FlowIdentity, string, int), Task<FlowDecision>> _asking = [];

    /// <summary>Creates the policy of a user.</summary>
    public NetworkPolicy(NetworkRuleBook rules, INetworkPermissionPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(prompt);
        _rules = rules;
        _prompt = prompt;
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
                asking = AskAsync(identity, vmName, host, port, cancellationToken);
                _asking[(identity, host, port)] = asking;
            }
        }

        try
        {
            return await asking.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_asking.TryGetValue((identity, host, port), out Task<FlowDecision>? current) && current == asking && asking.IsCompleted)
                {
                    _asking.Remove((identity, host, port));
                }
            }
        }
    }

    private async Task<FlowDecision> AskAsync(FlowIdentity identity, string vmName, string host, int port, CancellationToken cancellationToken)
    {
        await Task.Yield(); // registered as "asking" before the window opens
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
