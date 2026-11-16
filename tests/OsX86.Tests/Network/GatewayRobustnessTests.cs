using OsX86.Network;
using OsX86.Prefixes;
using OsX86.Tests.Vm;

namespace OsX86.Tests.Network;

/// <summary>
/// Work item 12, after the review: a decision that never comes, a relay that gives up on its own, a
/// window that fails, an answer for "this connection" never reused, a session answer that never
/// deletes a written rule, one Ethernet address per VM, refusals journaled once a minute.
/// </summary>
[Trait("Category", "unit")]
public sealed class GatewayRobustnessTests
{
    private static readonly PrefixId P1 = new("p1");
    private static readonly FlowIdentity Identity = new(NetworkRig.Vm, P1);

    [Fact]
    public async Task A_connection_whose_window_is_never_answered_is_refused_after_the_decision_time_and_frees_its_slot()
    {
        var time = new ManualTimeProvider();
        await using var hub = new NetworkHub(new InMemoryNetworkStore(), new LocalTestRelay(), new SilentPrompt(), new NetworkLimits { MaxFlowsPerIdentity = 1 }, time);
        UserNetwork network = hub.For(NetworkRig.User);
        var port = new RecordingPort();
        VmNetworkAttachment vm = network.Attach(NetworkRig.Vm, "base", port);
        var tcp = new GuestTcp(vm, port, NetworkRig.GuestMac, vm.AddressOf(P1), 40000, NetworkRig.Remote, 80);

        tcp.Send(TcpFlags.Syn);
        await VmTestRig.WaitUntilAsync(() => network.FlowCount == 1, "the flow waiting for its decision");
        time.Advance(TimeSpan.FromSeconds(121));

        SegmentSeen reset = await port.NextSegmentAsync();
        Assert.True(reset.Has(TcpFlags.Rst));
        Assert.Equal(0, network.FlowCount);
        NetworkJournalEntry entry = Assert.Single(await JournalAsync(network, 1));
        Assert.Equal((NetworkJournalEvent.Refused, NetworkJournalReasons.Undecided), (entry.Event, entry.Reason));
    }

    [Fact]
    public async Task A_relay_that_gives_up_by_itself_and_a_window_that_fails_refuse_the_connection_at_once()
    {
        var errors = new List<Exception>();
        await using var hub = new NetworkHub(new InMemoryNetworkStore(), new GivingUpRelay(), new FailingPrompt());
        hub.BackgroundError += (_, error) => errors.Add(error);
        UserNetwork network = hub.For(NetworkRig.User);
        var port = new RecordingPort();
        VmNetworkAttachment vm = network.Attach(NetworkRig.Vm, "base", port);

        // The window fails: refused, the error reported.
        SegmentSeen reset = await new GuestTcp(vm, port, NetworkRig.GuestMac, vm.AddressOf(P1), 40000, NetworkRig.Remote, 80).OpenAsync();
        Assert.True(reset.Has(TcpFlags.Rst));
        Assert.Single(errors);

        // A rule allows; the relay's own timeout: refused as unreachable.
        await network.Rules.AddAsync(Identity, NetworkDestination.Any, NetworkRuleAction.Allow, session: false, CancellationToken.None);
        reset = await new GuestTcp(vm, port, NetworkRig.GuestMac, vm.AddressOf(P1), 40001, NetworkRig.Remote, 80).OpenAsync();
        Assert.True(reset.Has(TcpFlags.Rst));

        IReadOnlyList<NetworkJournalEntry> journal = await JournalAsync(network, 2);
        Assert.Equal(NetworkJournalReasons.Undecided, journal[0].Reason);
        Assert.Equal((NetworkJournalReasons.Relay, RelayException.Unreachable), (journal[1].Reason, journal[1].Detail));
        Assert.Equal(0, network.FlowCount);
    }

    [Fact]
    public async Task An_answer_for_this_connection_is_never_reused_by_a_later_one_even_when_the_first_one_left()
    {
        var gate = new TaskCompletionSource<NetworkPermissionAnswer?>();
        var prompt = new CountingPrompt(gate.Task);
        var policy = new NetworkPolicy(new NetworkRuleBook(NetworkRig.User, new InMemoryNetworkStore(), TimeProvider.System), prompt);

        using (var left = new CancellationTokenSource())
        {
            Task<FlowDecision> waiting = policy.DecideAsync(Identity, "base", "203.0.113.9", 80, left.Token);
            await VmTestRig.WaitUntilAsync(() => prompt.Calls == 1, "the window open");
            await left.CancelAsync(); // its VM stopped
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        gate.SetResult(NetworkPermissionAnswer.AllowOnce); // answered after the connection left
        await VmTestRig.WaitUntilAsync(() => prompt.Answered, "the answer");
        await Task.Delay(50); // the shared question is forgotten once answered
        Task<FlowDecision> later = policy.DecideAsync(Identity, "base", "203.0.113.9", 80, CancellationToken.None);
        await VmTestRig.WaitUntilAsync(() => prompt.Calls == 2, "a new window for the later connection");
        Assert.True((await later).Allowed);
    }

    [Fact]
    public void A_session_rule_outranks_a_written_rule_of_the_same_scope_in_both_directions()
    {
        var web = new NetworkDestination("203.0.113.9", 80);
        var written = new NetworkRule(1, NetworkRig.Vm, P1, web, NetworkRuleAction.Deny, default);
        var session = new NetworkRule(-1, NetworkRig.Vm, P1, web, NetworkRuleAction.Allow, default, Session: true);
        Assert.Equal(-1, NetworkRuleMatcher.Decide([written, session], Identity, "203.0.113.9", 80).Rule!.Id);
        Assert.Equal(-1, NetworkRuleMatcher.Decide([session with { Action = NetworkRuleAction.Deny }, written with { Action = NetworkRuleAction.Allow }], Identity, "203.0.113.9", 80).Rule!.Id);
    }

    [Fact]
    public async Task A_vm_keeps_one_ethernet_address_and_repeated_refusals_are_journaled_once_a_minute()
    {
        await using var rig = new NetworkRig();
        Assert.True((await rig.Tcp(P1, remotePort: 25).OpenAsync()).Has(TcpFlags.Rst));
        Assert.True((await rig.Tcp(P1, localPort: 40001, remotePort: 25).OpenAsync()).Has(TcpFlags.Rst));

        // Another Ethernet address from the same VM: dropped, whatever its IP address.
        byte[] other = PacketBuilder.Tcp(new TcpEndpoints(new MacAddress(0x02_00_00_00_00_AAUL), AddressPlan.GatewayMac, rig.Attachment.Address, NetworkRig.Remote, 40002, 80), 1, 0, TcpFlags.Syn, 65535, []);
        rig.Attachment.Receive(other);
        Assert.False(await rig.Port.AnyWithinAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, rig.Network.Drops[DropReasons.Spoofed]);

        Assert.Single(await rig.JournalAsync(1));
        await Task.Delay(200);
        Assert.Single(await rig.Network.Journal.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void The_limits_refuse_a_decision_time_that_makes_no_sense() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkLimits { DecisionTimeout = TimeSpan.Zero }.Validate());

    private static async Task<IReadOnlyList<NetworkJournalEntry>> JournalAsync(UserNetwork network, int count)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            IReadOnlyList<NetworkJournalEntry> entries = await network.Journal.ReadAsync(CancellationToken.None);
            if (entries.Count >= count)
            {
                return entries;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"The journal did not reach {count} entries.");
    }

    private sealed class SilentPrompt : INetworkPermissionPrompt
    {
        public async ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    private sealed class FailingPrompt : INetworkPermissionPrompt
    {
        public ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the window could not be shown");
    }

    private sealed class GivingUpRelay : IRelayConnector
    {
        public ValueTask<IRelayStream> ConnectAsync(RelayFlowRequest request, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("the relay's own timeout");
    }

    private sealed class CountingPrompt(Task<NetworkPermissionAnswer?> first) : INetworkPermissionPrompt
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Answered { get; private set; }

        public async ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                NetworkPermissionAnswer? answer = await first;
                Answered = true;
                return answer;
            }

            return NetworkPermissionAnswer.AllowOnce;
        }
    }
}
