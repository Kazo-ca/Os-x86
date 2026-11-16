using System.Text;
using OsX86.Network;
using OsX86.Prefixes;

namespace OsX86.Tests.Network;

/// <summary>
/// Work item 12: the gateway ends a guest's TCP, decides it (rules, then the window), holds the
/// limits, opens the flow through the relay with the identity of the source, and journals it.
/// The milestone 0 scenario: a connection opens the window; allow passes, refuse blocks, the
/// journal records both.
/// </summary>
[Trait("Category", "unit")]
public sealed class GatewayTests
{
    private static readonly PrefixId P1 = new("p1");
    private static readonly PrefixId P2 = new("p2");

    [Fact]
    public async Task A_first_connection_opens_the_window_allow_passes_through_the_relay_and_the_journal_records_it()
    {
        await using var rig = new NetworkRig(_ => NetworkPermissionAnswer.AllowOnce);
        GuestTcp tcp = rig.Tcp(P1);

        SegmentSeen synAck = await tcp.OpenAsync();
        Assert.True(synAck.Has(TcpFlags.Syn | TcpFlags.Ack));
        Assert.True(synAck.ChecksumOk);
        Assert.Equal((ushort?)TcpFlow.MaxSegment, synAck.Mss);
        Assert.Equal((NetworkRig.Remote, rig.Attachment.AddressOf(P1)), (synAck.Source, synAck.Destination));

        NetworkPermissionRequest asked = Assert.Single(rig.Prompt.Requests);
        Assert.Equal((NetworkRig.User, new FlowIdentity(NetworkRig.Vm, P1), "base", "203.0.113.9", 80), (asked.UserId, asked.Identity, asked.VmName, asked.Host, asked.Port));
        RelayFlowRequest relayed = Assert.Single(rig.Relay.Requests);
        Assert.Equal("prefix:11111111111111111111111111111111:p1", relayed.Identity.RelayLabel);

        tcp.Write(Encoding.ASCII.GetBytes("GET /hello HTTP/1.0\r\n\r\n"));
        SegmentSeen end = await tcp.ReadToEndAsync();
        Assert.True(end.Has(TcpFlags.Fin));
        string answer = Encoding.ASCII.GetString([.. tcp.Received]);
        Assert.StartsWith("HTTP/1.0 200 OK", answer, StringComparison.Ordinal);
        Assert.EndsWith("osx86-test-relay 203.0.113.9:80 /hello\n", answer, StringComparison.Ordinal);
        tcp.Finish();

        IReadOnlyList<NetworkJournalEntry> journal = await rig.JournalAsync(2);
        Assert.Equal((NetworkJournalEvent.Opened, NetworkJournalReasons.Prompt), (journal[0].Event, journal[0].Reason));
        Assert.Equal((NetworkJournalEvent.Closed, NetworkJournalReasons.Ended), (journal[1].Event, journal[1].Reason));
        Assert.Equal(P1, journal[1].Prefix);
        Assert.Equal(23, journal[1].BytesOut);
        Assert.Equal(answer.Length, journal[1].BytesIn);
        Assert.Equal(0, rig.Network.FlowCount);
        Assert.Empty(await rig.Network.Rules.ListAsync(CancellationToken.None)); // "once": nothing remembered
    }

    [Fact]
    public async Task Refuse_resets_the_connection_never_reaches_the_relay_and_is_journaled()
    {
        await using var rig = new NetworkRig(_ => NetworkPermissionAnswer.DenyOnce);
        SegmentSeen reset = await rig.Tcp(P2).OpenAsync();

        Assert.True(reset.Has(TcpFlags.Rst));
        Assert.Empty(rig.Relay.Requests);
        NetworkJournalEntry entry = Assert.Single(await rig.JournalAsync(1));
        Assert.Equal((NetworkJournalEvent.Refused, NetworkJournalReasons.Prompt, P2, "203.0.113.9", 80), (entry.Event, entry.Reason, entry.Prefix, entry.Host, entry.Port));
    }

    [Fact]
    public async Task Two_prefixes_get_two_decisions_and_an_always_answer_becomes_a_rule()
    {
        await using var rig = new NetworkRig(request => request.Identity.Prefix == P1
            ? new NetworkPermissionAnswer(true, PermissionDuration.Always)
            : new NetworkPermissionAnswer(false, PermissionDuration.Session, AnyDestination: true));

        Assert.True((await rig.Tcp(P1).OpenAsync()).Has(TcpFlags.Syn));
        Assert.True((await rig.Tcp(P2, localPort: 40001).OpenAsync()).Has(TcpFlags.Rst));
        IReadOnlyList<NetworkRule> rules = await rig.Network.Rules.ListAsync(CancellationToken.None);
        Assert.Equal(2, rules.Count);
        Assert.Equal((P1, NetworkRuleAction.Allow, "203.0.113.9:80", false), (rules[0].Prefix!.Value, rules[0].Action, rules[0].Destination.ToString(), rules[0].Session));
        Assert.Equal((P2, NetworkRuleAction.Deny, "any", true), (rules[1].Prefix!.Value, rules[1].Action, rules[1].Destination.ToString(), rules[1].Session));
        Assert.Contains("\"prefix\": \"p1\"", await rig.Store.ReadAsync(NetworkRig.User, NetworkDocumentKind.Rules, CancellationToken.None), StringComparison.Ordinal);

        // The rules decide now: no new window.
        Assert.True((await rig.Tcp(P1, localPort: 40002).OpenAsync()).Has(TcpFlags.Syn));
        Assert.True((await rig.Tcp(P2, localPort: 40003, remotePort: 443).OpenAsync()).Has(TcpFlags.Rst));
        Assert.Equal(2, rig.Prompt.Requests.Count);
        IReadOnlyList<NetworkJournalEntry> journal = await rig.JournalAsync(4);
        Assert.Equal(NetworkJournalReasons.Rule, journal[2].Reason);
        Assert.Equal(rules[0].Id, journal[2].Rule);
        Assert.Equal((NetworkJournalEvent.Refused, rules[1].Id), (journal[3].Event, journal[3].Rule));
    }

    [Fact]
    public async Task Without_a_window_an_undecided_connection_is_refused_and_a_rule_still_decides()
    {
        await using var rig = new NetworkRig(_ => null);
        Assert.True((await rig.Tcp(P1).OpenAsync()).Has(TcpFlags.Rst));
        Assert.Equal(NetworkJournalReasons.NoPrompt, (await rig.JournalAsync(1))[0].Reason);

        await rig.Network.Rules.AddAsync(new FlowIdentity(NetworkRig.Vm, P1), NetworkDestination.Any, NetworkRuleAction.Allow, session: false, CancellationToken.None);
        Assert.True((await rig.Tcp(P1, localPort: 40001).OpenAsync()).Has(TcpFlags.Syn));
    }

    [Fact]
    public async Task The_limits_hold_ports_flows_per_identity_and_per_user()
    {
        var limits = new NetworkLimits { MaxFlowsPerIdentity = 2, MaxFlowsPerUser = 3 };
        await using var rig = new NetworkRig(_ => NetworkPermissionAnswer.AllowOnce, limits);

        Assert.True((await rig.Tcp(P1, remotePort: 25).OpenAsync()).Has(TcpFlags.Rst));
        Assert.True((await rig.Tcp(P1, localPort: 40001).OpenAsync()).Has(TcpFlags.Syn));
        Assert.True((await rig.Tcp(P1, localPort: 40002).OpenAsync()).Has(TcpFlags.Syn));
        Assert.True((await rig.Tcp(P1, localPort: 40003).OpenAsync()).Has(TcpFlags.Rst)); // a third for p1
        Assert.True((await rig.Tcp(P2, localPort: 40004).OpenAsync()).Has(TcpFlags.Syn));
        Assert.True((await rig.Tcp(P2, localPort: 40005).OpenAsync()).Has(TcpFlags.Rst)); // a fourth for the user

        IReadOnlyList<NetworkJournalEntry> journal = await rig.JournalAsync(6);
        Assert.Equal(
            [NetworkJournalReasons.Port, NetworkJournalReasons.FlowsPerIdentity, NetworkJournalReasons.FlowsPerUser],
            journal.Where(e => e.Event == NetworkJournalEvent.Refused).Select(e => e.Reason));
        Assert.Equal(3, rig.Network.FlowCount);
        Assert.Equal(3, rig.Relay.Requests.Count);
    }

    [Fact]
    public async Task An_idle_flow_is_cut_after_the_limit_and_retransmissions_follow_a_lost_segment()
    {
        var time = new Vm.ManualTimeProvider();
        await using var rig = new NetworkRig(_ => NetworkPermissionAnswer.AllowOnce, new NetworkLimits { IdleTimeout = TimeSpan.FromSeconds(5) }, time);
        GuestTcp tcp = rig.Tcp(P1);
        SegmentSeen synAck = await tcp.OpenAsync();
        Assert.True(synAck.Has(TcpFlags.Syn));

        // The request goes; the answer comes back; the guest "loses" it and says nothing.
        tcp.Write(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n"));
        SegmentSeen first;
        do
        {
            first = await rig.Port.NextSegmentAsync();
        }
        while (first.Payload.Length == 0);

        time.Advance(TimeSpan.FromSeconds(1)); // past the retransmission timeout
        SegmentSeen again;
        do
        {
            again = await rig.Port.NextSegmentAsync();
        }
        while (again.Payload.Length == 0);
        Assert.Equal(first.Sequence, again.Sequence);
        Assert.Equal(first.Payload, again.Payload);

        time.Advance(TimeSpan.FromSeconds(6));
        SegmentSeen reset;
        do
        {
            reset = await rig.Port.NextSegmentAsync();
        }
        while (!reset.Has(TcpFlags.Rst));

        IReadOnlyList<NetworkJournalEntry> journal = await rig.JournalAsync(2);
        Assert.Equal((NetworkJournalEvent.Closed, NetworkJournalReasons.Idle), (journal[1].Event, journal[1].Reason));
    }

    [Fact]
    public async Task Udp_and_dns_never_leave_and_are_journaled_once_a_minute()
    {
        await using var rig = new NetworkRig();
        Ipv4Address source = rig.Attachment.AddressOf(P1);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            rig.Attachment.Receive(Udp(source, Ipv4Address.Of(8, 8, 8, 8), 53));
        }

        rig.Attachment.Receive(Udp(source, NetworkRig.Remote, 123));
        IReadOnlyList<NetworkJournalEntry> journal = await rig.JournalAsync(2);
        Assert.Equal(2, journal.Count);
        Assert.All(journal, entry => Assert.Equal((NetworkJournalEvent.Refused, NetworkJournalReasons.Udp), (entry.Event, entry.Reason)));
        Assert.Equal("udp/53 (dns)", journal[0].Detail);
        Assert.Equal(4, rig.Network.Drops[DropReasons.Udp]);
        Assert.Empty(rig.Relay.Requests);
        Assert.Empty(rig.Prompt.Requests);
    }

    [Fact]
    public async Task An_unknown_destination_fails_at_the_test_relay_which_never_reaches_the_internet()
    {
        await using var rig = new NetworkRig();
        SegmentSeen reset = await rig.Tcp(P1, remote: Ipv4Address.Of(1, 1, 1, 1)).OpenAsync();
        Assert.True(reset.Has(TcpFlags.Rst));
        NetworkJournalEntry entry = Assert.Single(await rig.JournalAsync(1));
        Assert.Equal((NetworkJournalEvent.Refused, NetworkJournalReasons.Relay, RelayException.Unreachable), (entry.Event, entry.Reason, entry.Detail));
    }

    [Fact]
    public async Task Segments_of_no_flow_are_reset_and_sources_of_nobody_dropped()
    {
        await using var rig = new NetworkRig();
        GuestTcp stray = rig.Tcp(null);
        stray.Send(TcpFlags.Ack, [1]);
        SegmentSeen reset = await rig.Port.NextSegmentAsync();
        Assert.True(reset.Has(TcpFlags.Rst));

        // A prefix address that was never given in this run: nobody.
        var nobody = new GuestTcp(rig.Attachment, rig.Port, NetworkRig.GuestMac, AddressPlan.PrefixAddress(2, 9), 40000, NetworkRig.Remote, 80);
        nobody.Send(TcpFlags.Syn);
        Assert.False(await rig.Port.AnyWithinAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, rig.Network.Drops[DropReasons.UnknownSource]);
    }

    [Fact]
    public async Task A_vm_that_stops_and_a_prefix_that_goes_end_their_flows()
    {
        await using var rig = new NetworkRig();
        Assert.True((await rig.Tcp(P1).OpenAsync()).Has(TcpFlags.Syn));
        rig.Attachment.Release(P1);
        SegmentSeen reset = await rig.Port.NextSegmentAsync();
        Assert.True(reset.Has(TcpFlags.Rst));
        Assert.Equal(0, rig.Network.FlowCount);

        Assert.True((await rig.Tcp(P2, localPort: 40001).OpenAsync()).Has(TcpFlags.Syn));
        await rig.Attachment.DisposeAsync();
        IReadOnlyList<NetworkJournalEntry> journal = await rig.JournalAsync(4);
        Assert.All(journal.Where(e => e.Event == NetworkJournalEvent.Closed), e => Assert.Equal(NetworkJournalReasons.VmStopped, e.Reason));
        Assert.Equal(0, rig.Network.FlowCount);
    }

    [Fact]
    public async Task A_large_answer_crosses_in_order_through_the_guests_window()
    {
        await using var rig = new NetworkRig();
        GuestTcp tcp = rig.Tcp(null);
        Assert.True((await tcp.OpenAsync(mss: 536, window: 8192)).Has(TcpFlags.Syn));
        tcp.Write(Encoding.ASCII.GetBytes("GET /blob?mb=1 HTTP/1.0\r\n\r\n"));
        SegmentSeen end = await tcp.ReadToEndAsync();
        Assert.True(end.Has(TcpFlags.Fin));
        string head = Encoding.ASCII.GetString([.. tcp.Received.Take(80)]);
        Assert.Contains("Content-Length: 1048576", head, StringComparison.Ordinal);
        Assert.True(tcp.Received.Count > 1048576);
        NetworkJournalEntry opened = (await rig.JournalAsync(1))[0];
        Assert.Null(opened.Prefix); // the VM itself: its own identity (D22)
    }

    private static byte[] Udp(Ipv4Address source, Ipv4Address destination, ushort port)
    {
        // An IPv4 packet of protocol 17 with a minimal UDP header, built from a TCP frame's layout.
        byte[] frame = PacketBuilder.Tcp(new TcpEndpoints(NetworkRig.GuestMac, AddressPlan.GatewayMac, source, destination, 5353, port), 0, 0, TcpFlags.None, 0, []);
        frame[EthernetFrame.HeaderLength + 9] = IpProtocols.Udp;
        return frame;
    }
}
