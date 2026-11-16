using OsX86.Interface;
using OsX86.Network;
using OsX86.Prefixes;
using OsX86.Tests.Vm;

namespace OsX86.Tests.Network;

/// <summary>Work item 12: rules (match, store, versioned format), journal (bounds, format), policy (one window for many).</summary>
[Trait("Category", "unit")]
public sealed class RulesAndJournalTests
{
    private static readonly Guid User = NetworkRig.User;
    private static readonly FlowIdentity P1 = new(NetworkRig.Vm, new PrefixId("p1"));
    private static readonly FlowIdentity Machine = new(NetworkRig.Vm, null);
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("any", null, null, "any")]
    [InlineData("*", null, null, "any")]
    [InlineData("203.0.113.9", "203.0.113.9", null, "203.0.113.9")]
    [InlineData("Example.TEST:443", "example.test", 443, "example.test:443")]
    [InlineData("*:80", null, 80, "*:80")]
    public void Destinations_read_and_print_back(string text, string? host, int? port, string printed)
    {
        Assert.True(NetworkDestination.TryParse(text, out NetworkDestination? destination, out _));
        Assert.Equal((host, port), (destination!.Host, destination.Port));
        Assert.Equal(printed, destination.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:http")]
    [InlineData("bad host")]
    [InlineData("-lead")]
    [InlineData("[2001:db8::1]:80")]
    public void Bad_destinations_are_refused_with_a_reason(string text)
    {
        Assert.False(NetworkDestination.TryParse(text, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void The_most_specific_rule_wins_and_deny_wins_a_tie()
    {
        NetworkRule Rule(int id, string destination, NetworkRuleAction action, FlowIdentity? who = null)
        {
            NetworkDestination.TryParse(destination, out NetworkDestination? parsed, out _);
            FlowIdentity identity = who ?? P1;
            return new NetworkRule(id, identity.VmId, identity.Prefix, parsed!, action, T0);
        }

        NetworkRule[] rules =
        [
            Rule(1, "any", NetworkRuleAction.Deny),
            Rule(2, "*:80", NetworkRuleAction.Ask),
            Rule(3, "203.0.113.9", NetworkRuleAction.Allow),
            Rule(4, "203.0.113.9:443", NetworkRuleAction.Deny),
            Rule(5, "203.0.113.9:443", NetworkRuleAction.Allow),
            Rule(6, "any", NetworkRuleAction.Allow, Machine),
        ];

        Assert.Equal(3, NetworkRuleMatcher.Decide(rules, P1, "203.0.113.9", 80).Rule!.Id);
        Assert.Equal(4, NetworkRuleMatcher.Decide(rules, P1, "203.0.113.9", 443).Rule!.Id);
        Assert.Equal(NetworkRuleAction.Ask, NetworkRuleMatcher.Decide(rules, P1, "198.51.100.1", 80).Action);
        Assert.Equal(1, NetworkRuleMatcher.Decide(rules, P1, "198.51.100.1", 443).Rule!.Id);
        Assert.Equal(6, NetworkRuleMatcher.Decide(rules, Machine, "198.51.100.1", 443).Rule!.Id);
        Assert.Equal(RuleVerdict.Undecided, NetworkRuleMatcher.Decide(rules, new FlowIdentity(Guid.NewGuid(), null), "x", 80));
    }

    [Fact]
    public async Task The_rule_book_writes_its_rules_keeps_session_ones_in_memory_and_reads_them_again()
    {
        var store = new InMemoryNetworkStore();
        var time = new ManualTimeProvider();
        var book = new NetworkRuleBook(User, store, time);
        NetworkDestination web = new("203.0.113.9", 80);

        NetworkRule first = await book.AddAsync(P1, web, NetworkRuleAction.Ask, session: false, CancellationToken.None);
        NetworkRule replaced = await book.AddAsync(P1, web, NetworkRuleAction.Allow, session: false, CancellationToken.None);
        NetworkRule session = await book.AddAsync(Machine, NetworkDestination.Any, NetworkRuleAction.Deny, session: true, CancellationToken.None);
        Assert.Equal((1, 2, -1), (first.Id, replaced.Id, session.Id));
        Assert.Equal([2, -1], (await book.ListAsync(CancellationToken.None)).Select(rule => rule.Id));

        // Another book on the same store: the written rule only, with its number.
        var reread = new NetworkRuleBook(User, store, time);
        NetworkRule kept = Assert.Single(await reread.ListAsync(CancellationToken.None));
        Assert.Equal((2, NetworkRuleAction.Allow, web), (kept.Id, kept.Action, kept.Destination));

        // A session answer for the same scope replaces the written rule.
        await book.AddAsync(P1, web, NetworkRuleAction.Deny, session: true, CancellationToken.None);
        Assert.Equal(NetworkRuleAction.Deny, (await book.DecideAsync(P1, "203.0.113.9", 80, CancellationToken.None)).Action);
        Assert.DoesNotContain("\"id\": 2", await store.ReadAsync(User, NetworkDocumentKind.Rules, CancellationToken.None), StringComparison.Ordinal);

        Assert.True(await book.RemoveAsync(-1, CancellationToken.None));
        Assert.False(await book.RemoveAsync(99, CancellationToken.None));
        NetworkRule written = await book.AddAsync(P1, NetworkDestination.Any, NetworkRuleAction.Allow, session: false, CancellationToken.None);
        Assert.True(await book.RemoveAsync(written.Id, CancellationToken.None));
        await book.AddAsync(P1, NetworkDestination.Any, NetworkRuleAction.Allow, session: false, CancellationToken.None);
        await book.AddAsync(Machine, NetworkDestination.Any, NetworkRuleAction.Allow, session: false, CancellationToken.None);
        Assert.Equal(2, await book.RemoveForAsync(NetworkRig.Vm, new PrefixId("p1"), CancellationToken.None)); // the session deny and the written allow
        Assert.Equal(1, await book.RemoveForAsync(NetworkRig.Vm, null, CancellationToken.None));
        Assert.Empty(await book.ListAsync(CancellationToken.None));
    }

    [Fact]
    public void The_rules_format_is_versioned_and_strict()
    {
        string valid = new NetworkRulesDocument
        {
            User = User,
            NextId = 2,
            Rules = [new NetworkRule(1, NetworkRig.Vm, new PrefixId("p1"), new NetworkDestination("203.0.113.9", 80), NetworkRuleAction.Allow, T0)],
        }.ToJson();
        Assert.Contains("\"format\": 1", valid, StringComparison.Ordinal);
        Assert.Equal("p1", NetworkRulesDocument.Parse(valid, User).Rules[0].Prefix!.Value.Value);

        Assert.Equal(X86ErrorCodes.UnsupportedFormat, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse(valid.Replace("\"format\": 1", "\"format\": 2", StringComparison.Ordinal), User)).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse(valid, Guid.NewGuid())).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse(valid.Replace("\"nextId\": 2", "\"nextId\": 1", StringComparison.Ordinal), User)).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse(valid.Replace("\"port\": 80", "\"port\": 0", StringComparison.Ordinal), User)).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse(valid.Replace("\"prefix\": \"p1\"", "\"prefix\": \"P 1\"", StringComparison.Ordinal), User)).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse("not json", User)).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkRulesDocument.Parse(new string(' ', NetworkRulesDocument.MaxBytes + 1), User)).Code);
    }

    [Fact]
    public async Task The_journal_keeps_a_week_and_a_thousand_entries_in_a_versioned_document()
    {
        var store = new InMemoryNetworkStore();
        var time = new ManualTimeProvider();
        var journal = new NetworkJournal(User, store, time);
        NetworkJournalEntry Entry(int port) => new() { Time = default, Vm = NetworkRig.Vm, Prefix = new PrefixId("p1"), Host = "203.0.113.9", Port = port, Event = NetworkJournalEvent.Refused, Reason = NetworkJournalReasons.Port };
        var written = new List<NetworkJournalEntry>();
        journal.Written += (_, entry) => written.Add(entry);

        await journal.AppendAsync(Entry(1), CancellationToken.None);
        time.Advance(TimeSpan.FromDays(8));
        await journal.AppendAsync(Entry(2), CancellationToken.None);
        IReadOnlyList<NetworkJournalEntry> entries = await new NetworkJournal(User, store, time).ReadAsync(CancellationToken.None);
        Assert.Equal([2], entries.Select(entry => entry.Port)); // the first is older than the retention
        Assert.Equal(time.GetUtcNow(), entries[0].Time);
        Assert.Equal(2, written.Count);

        IReadOnlyList<NetworkJournalEntry> many = [];
        for (int index = 0; index < NetworkJournalDocument.MaxEntries + 5; index++)
        {
            many = NetworkJournalDocument.Append(many, Entry(index) with { Time = time.GetUtcNow() }, time.GetUtcNow());
        }

        Assert.Equal(NetworkJournalDocument.MaxEntries, many.Count);
        Assert.Equal(5, many[0].Port);

        string json = new NetworkJournalDocument { User = User, Entries = entries }.ToJson();
        Assert.Contains("\"event\":\"Refused\"", json, StringComparison.Ordinal);
        Assert.Equal(X86ErrorCodes.UnsupportedFormat, Assert.Throws<X86Exception>(() => NetworkJournalDocument.Parse(json.Replace("\"format\":1", "\"format\":9", StringComparison.Ordinal), User)).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => NetworkJournalDocument.Parse(json, Guid.NewGuid())).Code);
    }

    [Fact]
    public async Task Connections_waiting_together_share_one_window_and_an_unavailable_window_refuses()
    {
        var gate = new TaskCompletionSource();
        var prompt = new GatedPrompt(gate.Task);
        var policy = new NetworkPolicy(new NetworkRuleBook(User, new InMemoryNetworkStore(), TimeProvider.System), prompt);

        Task<FlowDecision> first = policy.DecideAsync(P1, "base", "203.0.113.9", 80, CancellationToken.None);
        Task<FlowDecision> second = policy.DecideAsync(P1, "base", "203.0.113.9", 80, CancellationToken.None);
        await Task.Delay(100);
        gate.SetResult();
        Assert.True((await first).Allowed);
        Assert.True((await second).Allowed);
        Assert.Equal(1, prompt.Calls);

        var none = new NetworkPolicy(new NetworkRuleBook(User, new InMemoryNetworkStore(), TimeProvider.System), UnavailablePermissionPrompt.Instance);
        Assert.Equal(new FlowDecision(false, NetworkJournalReasons.NoPrompt, null), await none.DecideAsync(P1, "base", "203.0.113.9", 80, CancellationToken.None));
    }

    [Fact]
    public async Task The_relays_without_kazo_refuse_and_the_test_relay_serves_only_what_it_knows()
    {
        RelayException unavailable = await Assert.ThrowsAsync<RelayException>(async () => await UnavailableRelay.Instance.ConnectAsync(new RelayFlowRequest(P1, "203.0.113.9", 80), CancellationToken.None));
        Assert.Equal(RelayException.Unavailable, unavailable.Code);
        Assert.Equal(RelayException.Unavailable, new RelayException().Code);
        Assert.Equal(RelayException.Unavailable, new RelayException("x", new InvalidOperationException()).Code);

        var relay = new LocalTestRelay().With("echo.test", 7, async (connection, token) =>
        {
            byte[] buffer = new byte[64];
            int count;
            while ((count = await connection.ReadAsync(buffer, token)) > 0)
            {
                await connection.WriteAsync(buffer.AsMemory(0, count), token);
            }
        });
        await using IRelayStream stream = await relay.ConnectAsync(new RelayFlowRequest(P1, "ECHO.test", 7), CancellationToken.None);
        await stream.WriteAsync("ping"u8.ToArray(), CancellationToken.None);
        await stream.CompleteWritesAsync(CancellationToken.None);
        byte[] back = new byte[16];
        int read = await stream.ReadAsync(back, CancellationToken.None);
        Assert.Equal("ping", System.Text.Encoding.ASCII.GetString(back, 0, read));
        Assert.Equal(0, await stream.ReadAsync(back, CancellationToken.None));
        Assert.Equal(RelayException.Unreachable, (await Assert.ThrowsAsync<RelayException>(async () => await relay.ConnectAsync(new RelayFlowRequest(P1, "203.0.113.9", 80), CancellationToken.None))).Code);
        Assert.Equal(2, relay.Requests.Count);
    }

    [Fact]
    public void The_limits_refuse_what_makes_no_sense()
    {
        new NetworkLimits().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkLimits { MaxFlowsPerIdentity = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkLimits { MaxFlowsPerUser = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new NetworkLimits { AllowedPorts = new HashSet<int> { 0 } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkLimits { IdleTimeout = TimeSpan.Zero }.Validate());
        Assert.Equal([80, 443], new NetworkLimits().AllowedPorts.Order());
    }

    private sealed class GatedPrompt(Task gate) : INetworkPermissionPrompt
    {
        private int _calls;

        public int Calls => _calls;

        public async ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await gate;
            return NetworkPermissionAnswer.AllowOnce;
        }
    }
}
