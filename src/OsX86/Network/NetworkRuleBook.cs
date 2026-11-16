using OsX86.Prefixes;

namespace OsX86.Network;

/// <summary>
/// The rules of one user: the written ones (<see cref="NetworkRulesDocument"/>, read on first use)
/// and the ones remembered for the session (memory only). Every change is written before it is
/// seen; one change at a time.
/// </summary>
public sealed class NetworkRuleBook
{
    private readonly INetworkStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private NetworkRulesDocument? _document;
    private List<NetworkRule> _session = [];
    private int _nextSessionId = -1;

    /// <summary>Creates the book of a user.</summary>
    public NetworkRuleBook(Guid userId, INetworkStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        UserId = userId;
        _store = store;
        _time = time;
    }

    /// <summary>The user.</summary>
    public Guid UserId { get; }

    /// <summary>Every rule, written ones first (by number), then the session ones.</summary>
    public async ValueTask<IReadOnlyList<NetworkRule>> ListAsync(CancellationToken cancellationToken)
    {
        NetworkRulesDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            return [.. document.Rules, .. _session];
        }
    }

    /// <summary>What the rules say of a connection.</summary>
    public async ValueTask<RuleVerdict> DecideAsync(FlowIdentity identity, string host, int port, CancellationToken cancellationToken) =>
        NetworkRuleMatcher.Decide(await ListAsync(cancellationToken).ConfigureAwait(false), identity, host, port);

    /// <summary>
    /// Adds a rule; a rule of the same identity and destination is replaced (the new answer wins,
    /// so "ask" then "always allow" does not ask again). A session rule is kept in memory only.
    /// </summary>
    public async ValueTask<NetworkRule> AddAsync(FlowIdentity identity, NetworkDestination destination, NetworkRuleAction action, bool session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkRulesDocument document = await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
            bool Same(NetworkRule rule) => rule.Identity == identity && rule.Destination == destination;
            DateTimeOffset now = _time.GetUtcNow();
            if (session)
            {
                lock (_lock)
                {
                    var rule = new NetworkRule(_nextSessionId--, identity.VmId, identity.Prefix, destination, action, now, Session: true);
                    _session = [.. _session.Where(existing => !Same(existing)), rule];
                }

                // A written rule of the same scope would outrank nothing (same specificity): it goes.
                if (document.Rules.Any(Same))
                {
                    await SaveAsync(new NetworkRulesDocument { User = UserId, NextId = document.NextId, Rules = [.. document.Rules.Where(rule => !Same(rule))] }, cancellationToken).ConfigureAwait(false);
                }

                lock (_lock)
                {
                    return _session[^1];
                }
            }

            var added = new NetworkRule(document.NextId, identity.VmId, identity.Prefix, destination, action, now);
            await SaveAsync(new NetworkRulesDocument { User = UserId, NextId = document.NextId + 1, Rules = [.. document.Rules.Where(rule => !Same(rule)), added] }, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _session = [.. _session.Where(existing => !Same(existing))];
            }

            return added;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a rule by its number; false when there is none.</summary>
    public async ValueTask<bool> RemoveAsync(int id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                int before = _session.Count;
                _session = [.. _session.Where(rule => rule.Id != id)];
                if (_session.Count != before)
                {
                    return true;
                }
            }

            NetworkRulesDocument document = await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
            if (!document.Rules.Any(rule => rule.Id == id))
            {
                return false;
            }

            await SaveAsync(new NetworkRulesDocument { User = UserId, NextId = document.NextId, Rules = [.. document.Rules.Where(rule => rule.Id != id)] }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the rules of a VM (all of them when <paramref name="prefix"/> is <see langword="null"/>)
    /// or of one of its prefixes: a deleted prefix or VM leaves nothing a new one of the same name would inherit.
    /// </summary>
    public async ValueTask<int> RemoveForAsync(Guid vmId, PrefixId? prefix, CancellationToken cancellationToken)
    {
        bool Covered(NetworkRule rule) => rule.VmId == vmId && (prefix is null || rule.Prefix == prefix);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int removed;
            lock (_lock)
            {
                removed = _session.Count(Covered);
                _session = [.. _session.Where(rule => !Covered(rule))];
            }

            NetworkRulesDocument document = await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
            int written = document.Rules.Count(Covered);
            if (written > 0)
            {
                await SaveAsync(new NetworkRulesDocument { User = UserId, NextId = document.NextId, Rules = [.. document.Rules.Where(rule => !Covered(rule))] }, cancellationToken).ConfigureAwait(false);
            }

            return removed + written;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<NetworkRulesDocument> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_document is not null)
            {
                return _document;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<NetworkRulesDocument> LoadUnderGateAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_document is not null)
            {
                return _document;
            }
        }

        string? json = await _store.ReadAsync(UserId, NetworkDocumentKind.Rules, cancellationToken).ConfigureAwait(false);
        NetworkRulesDocument document = json is null ? new NetworkRulesDocument { User = UserId } : NetworkRulesDocument.Parse(json, UserId);
        lock (_lock)
        {
            _document ??= document;
            return _document;
        }
    }

    private async ValueTask SaveAsync(NetworkRulesDocument document, CancellationToken cancellationToken)
    {
        await _store.WriteAsync(UserId, NetworkDocumentKind.Rules, document.ToJson(), cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _document = document;
        }
    }
}

/// <summary>
/// The journal of one user: every decision and every end of a flow, written as it happens (read on
/// first use, bounded by <see cref="NetworkJournalDocument.MaxEntries"/> and its retention).
/// </summary>
public sealed class NetworkJournal
{
    private readonly INetworkStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<NetworkJournalEntry>? _entries;

    /// <summary>Creates the journal of a user.</summary>
    public NetworkJournal(Guid userId, INetworkStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        UserId = userId;
        _store = store;
        _time = time;
    }

    /// <summary>The user.</summary>
    public Guid UserId { get; }

    /// <summary>Raised after an entry is written (the page of the rules refreshes; the tests wait on it).</summary>
    public event EventHandler<NetworkJournalEntry>? Written;

    /// <summary>The entries, oldest first.</summary>
    public async ValueTask<IReadOnlyList<NetworkJournalEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes an entry (its time is set now).</summary>
    public async ValueTask AppendAsync(NetworkJournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DateTimeOffset now = _time.GetUtcNow();
        NetworkJournalEntry stamped = entry with { Time = now };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<NetworkJournalEntry> entries = NetworkJournalDocument.Append(await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false), stamped, now);
            await _store.WriteAsync(UserId, NetworkDocumentKind.Journal, new NetworkJournalDocument { User = UserId, Entries = entries }.ToJson(), cancellationToken).ConfigureAwait(false);
            _entries = entries;
        }
        finally
        {
            _gate.Release();
        }

        Written?.Invoke(this, stamped);
    }

    private async ValueTask<IReadOnlyList<NetworkJournalEntry>> LoadUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_entries is null)
        {
            string? json = await _store.ReadAsync(UserId, NetworkDocumentKind.Journal, cancellationToken).ConfigureAwait(false);
            _entries = json is null ? [] : NetworkJournalDocument.Parse(json, UserId).Entries;
        }

        return _entries;
    }
}
