using System.Buffers.Binary;
using OsX86.Interface;
using OsX86.Network;
using OsX86.Prefixes;

namespace OsX86.Tests.Network;

/// <summary>Work item 12: the address plan, the frames, and the switch of a user (161 A, E6, 146).</summary>
[Trait("Category", "unit")]
public sealed class AddressAndSwitchTests
{
    private static readonly MacAddress OtherMac = new(0x02_00_00_00_00_03UL);

    [Fact]
    public void The_plan_gives_each_vm_and_prefix_its_own_address_and_reads_them_back()
    {
        Assert.Equal("10.86.0.1", AddressPlan.Gateway.ToString());
        Assert.Equal("10.86.0.2", AddressPlan.VmAddress(2).ToString());
        Assert.Equal("10.86.7.2", AddressPlan.PrefixAddress(7, 0).ToString());
        Assert.Equal("10.86.7.254", AddressPlan.PrefixAddress(7, 63).ToString());
        Assert.Equal("10.86.7.5", AddressPlan.PrefixLinkAddress(7, 1).ToString());

        Assert.True(AddressPlan.TryResolve(Ipv4Address.Of(10, 86, 0, 9), out int vm, out int? prefix));
        Assert.Equal((9, (int?)null), (vm, prefix));
        Assert.True(AddressPlan.TryResolve(Ipv4Address.Of(10, 86, 9, 6), out vm, out prefix));
        Assert.Equal((9, (int?)1), (vm, prefix));
        Assert.False(AddressPlan.TryResolve(Ipv4Address.Of(10, 86, 9, 5), out _, out _)); // the VM side of a link
        Assert.False(AddressPlan.TryResolve(AddressPlan.Gateway, out _, out _));
        Assert.False(AddressPlan.TryResolve(Ipv4Address.Of(10, 86, 0, 255), out _, out _));
        Assert.False(AddressPlan.TryResolve(Ipv4Address.Of(203, 0, 113, 9), out _, out _));

        Assert.True(AddressPlan.MaySend(3, Ipv4Address.Of(10, 86, 0, 3)));
        Assert.True(AddressPlan.MaySend(3, Ipv4Address.Of(10, 86, 3, 10)));
        Assert.False(AddressPlan.MaySend(3, Ipv4Address.Of(10, 86, 0, 4)));
        Assert.False(AddressPlan.MaySend(3, Ipv4Address.Of(10, 86, 4, 2)));
        Assert.True(AddressPlan.IsPrivate(Ipv4Address.Of(10, 86, 200, 1)));
        Assert.False(AddressPlan.IsPrivate(Ipv4Address.Of(10, 87, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AddressPlan.VmAddress(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AddressPlan.PrefixAddress(2, 64));
    }

    [Theory]
    [InlineData("203.0.113.9", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("01.2.3.4", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Addresses_are_read_strictly(string? text, bool valid)
    {
        Assert.Equal(valid, Ipv4Address.TryParse(text, out Ipv4Address address));
        if (valid)
        {
            Assert.Equal(text, address.ToString());
        }
    }

    [Fact]
    public void The_identity_label_names_the_vm_or_its_prefix_in_the_characters_the_relay_takes()
    {
        var vm = Guid.Parse("0123456789abcdef0123456789abcdef");
        Assert.Equal("vm:0123456789abcdef0123456789abcdef", new FlowIdentity(vm, null).RelayLabel);
        string label = new FlowIdentity(vm, new PrefixId("wine-1")).ToString();
        Assert.Equal("prefix:0123456789abcdef0123456789abcdef:wine-1", label);
        Assert.Matches("^[A-Za-z0-9._:-]{1,128}$", label);
    }

    [Fact]
    public void A_built_tcp_segment_reads_back_with_valid_checksums_and_its_option()
    {
        var endpoints = new TcpEndpoints(AddressPlan.GatewayMac, NetworkRig.GuestMac, NetworkRig.Remote, AddressPlan.VmAddress(2), 80, 40000);
        byte[] frame = PacketBuilder.Tcp(endpoints, 7, 9, TcpFlags.Syn | TcpFlags.Ack, 1234, [1, 2, 3], 1460);

        Assert.True(SegmentSeen.TryRead(frame, out SegmentSeen? seen));
        Assert.True(seen!.ChecksumOk);
        Assert.Equal((7u, 9u, (ushort)1234, (ushort?)1460), (seen.Sequence, seen.Acknowledgment, seen.Window, seen.Mss));
        Assert.Equal([1, 2, 3], seen.Payload);
        Assert.True(EthernetFrame.TryRead(frame, out EthernetFrame ethernet));
        Assert.True(Ipv4Packet.TryRead(ethernet.Payload, out Ipv4Packet ip));
        Assert.Equal(0, PacketBuilder.Checksum(ethernet.Payload[..20], 0)); // the IP header's own checksum
        Assert.False(ip.IsFragment);
        Assert.False(EthernetFrame.TryRead(new byte[10], out _));
        Assert.False(Ipv4Packet.TryRead(new byte[19], out _));
        Assert.False(TcpSegment.TryRead(new byte[19], out _));
        Assert.Equal("52:54:00:86:00:01", AddressPlan.GatewayMac.ToString());
    }

    [Fact]
    public async Task The_gateway_answers_arp_and_ping_for_its_address_only()
    {
        await using var rig = new NetworkRig();
        rig.Attachment.Receive(Arp(rig.Attachment.Address, AddressPlan.Gateway));
        byte[] reply = await rig.Port.NextAsync();
        Assert.True(EthernetFrame.TryRead(reply, out EthernetFrame frame));
        Assert.True(ArpPacket.TryRead(frame.Payload, out ArpPacket arp));
        Assert.Equal((ArpPacket.Reply, AddressPlan.GatewayMac, AddressPlan.Gateway), (arp.Operation, arp.SenderMac, arp.SenderIp));

        rig.Attachment.Receive(Ping(rig.Attachment.Address, AddressPlan.Gateway));
        byte[] pong = await rig.Port.NextAsync();
        Assert.True(EthernetFrame.TryRead(pong, out frame));
        Assert.True(Ipv4Packet.TryRead(frame.Payload, out Ipv4Packet ip));
        Assert.Equal((IpProtocols.Icmp, AddressPlan.Gateway, (byte)0), (ip.Protocol, ip.Source, ip.Payload[0]));
        Assert.Equal(0, PacketBuilder.Checksum(ip.Payload, 0));

        // An ARP request for an address nobody has, and a ping out of the network: nothing comes back.
        rig.Attachment.Receive(Arp(rig.Attachment.Address, Ipv4Address.Of(10, 86, 0, 77)));
        rig.Attachment.Receive(Ping(rig.Attachment.Address, NetworkRig.Remote));
        Assert.False(await rig.Port.AnyWithinAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, rig.Network.Drops[DropReasons.Isolation]);
        Assert.Equal(1, rig.Network.Drops[DropReasons.Protocol]);
    }

    [Fact]
    public async Task Frames_with_another_vms_address_ipv6_or_garbage_are_dropped()
    {
        await using var rig = new NetworkRig();
        rig.Attachment.Receive(Ping(Ipv4Address.Of(10, 86, 0, 3), AddressPlan.Gateway)); // not this VM's address
        rig.Attachment.Receive(Arp(Ipv4Address.Of(10, 86, 5, 2), AddressPlan.Gateway)); // another VM's prefix
        byte[] ipv6 = Ping(rig.Attachment.Address, AddressPlan.Gateway);
        BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(12), EtherTypes.Ipv6);
        rig.Attachment.Receive(ipv6);
        rig.Attachment.Receive(new byte[8]);
        byte[] other = Ping(rig.Attachment.Address, AddressPlan.Gateway);
        BinaryPrimitives.WriteUInt16BigEndian(other.AsSpan(12), 0x88B5);
        rig.Attachment.Receive(other);

        Assert.False(await rig.Port.AnyWithinAsync(TimeSpan.FromMilliseconds(200)));
        IReadOnlyDictionary<string, long> drops = rig.Network.Drops;
        Assert.Equal(2, drops[DropReasons.Spoofed]);
        Assert.Equal(1, drops[DropReasons.Ipv6]);
        Assert.Equal(1, drops[DropReasons.Malformed]);
        Assert.Equal(1, drops[DropReasons.OtherType]);
    }

    [Fact]
    public async Task Two_vms_of_a_user_reach_each_other_but_their_prefixes_never_cross()
    {
        await using var rig = new NetworkRig();
        var otherPort = new RecordingPort();
        VmNetworkAttachment other = rig.Network.Attach(Guid.NewGuid(), "second", otherPort);
        Assert.Equal(3, other.VmSlot);
        Assert.Equal([2, 3], rig.Network.Attached.Select(vm => vm.VmSlot));
        Assert.Throws<InvalidOperationException>(() => rig.Network.Attach(NetworkRig.Vm, "again", new RecordingPort()));

        // VM 2 asks who has VM 3's address: only VM 3 hears it, and answers.
        rig.Attachment.Receive(Arp(rig.Attachment.Address, other.Address));
        byte[] request = await otherPort.NextAsync();
        other.Receive(ArpReply(OtherMac, other.Address, NetworkRig.GuestMac, rig.Attachment.Address));
        Assert.NotEmpty(await rig.Port.NextAsync());
        Assert.NotEmpty(request);

        // VM to VM: carried. A prefix of VM 2 to VM 3: dropped (146).
        var endpoints = new TcpEndpoints(NetworkRig.GuestMac, OtherMac, rig.Attachment.Address, other.Address, 40000, 7000);
        rig.Attachment.Receive(PacketBuilder.Tcp(endpoints, 1, 0, TcpFlags.Syn, 65535, []));
        SegmentSeen carried = await otherPort.NextSegmentAsync();
        Assert.Equal((rig.Attachment.Address, (ushort)7000), (carried.Source, carried.DestinationPort));

        Ipv4Address prefix = rig.Attachment.AddressOf(new PrefixId("p1"));
        rig.Attachment.Receive(PacketBuilder.Tcp(endpoints with { SourceIp = prefix }, 1, 0, TcpFlags.Syn, 65535, []));
        Assert.False(await otherPort.AnyWithinAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, rig.Network.Drops[DropReasons.Isolation]);

        // VM 3 cannot take VM 2's Ethernet address while VM 2 is plugged in; after it leaves, VM 2's address is free.
        other.Receive(PacketBuilder.Tcp(endpoints with { SourceMac = NetworkRig.GuestMac, SourceIp = other.Address, DestinationMac = AddressPlan.GatewayMac }, 1, 0, TcpFlags.Syn, 65535, []));
        Assert.Equal(1, rig.Network.Drops[DropReasons.Spoofed]);
        await rig.Attachment.DisposeAsync();
        Assert.Single(rig.Network.Attached);
        Assert.Null(rig.Network.Find(NetworkRig.Vm));
        Assert.Same(other, rig.Network.Find(other.VmId));
    }

    [Fact]
    public async Task Users_never_share_a_switch_and_slots_run_out_cleanly()
    {
        await using var rig = new NetworkRig();
        UserNetwork otherUser = rig.Hub.For(Guid.NewGuid());
        Assert.NotSame(rig.Network, otherUser);
        Assert.Same(rig.Network, rig.Hub.For(NetworkRig.User));
        VmNetworkAttachment first = otherUser.Attach(Guid.NewGuid(), "x", new RecordingPort());
        Assert.Equal(2, first.VmSlot); // the same plan in another user's network: never on the same switch
        Assert.Throws<ArgumentException>(() => rig.Hub.For(Guid.Empty));

        for (int slot = 3; slot <= AddressPlan.LastVmSlot; slot++)
        {
            otherUser.Attach(Guid.NewGuid(), "vm" + slot, new RecordingPort());
        }

        X86Exception full = Assert.Throws<X86Exception>(() => otherUser.Attach(Guid.NewGuid(), "one-more", new RecordingPort()));
        Assert.Equal(X86ErrorCodes.NetworkUnavailable, full.Code);

        for (int index = 0; index < AddressPlan.PrefixSlots; index++)
        {
            Assert.Equal(index, first.SlotOf(new PrefixId("p" + index)));
        }

        Assert.Equal(0, first.SlotOf(new PrefixId("p0")));
        Assert.Equal(X86ErrorCodes.NetworkUnavailable, Assert.Throws<X86Exception>(() => first.SlotOf(new PrefixId("extra"))).Code);
        first.Release(new PrefixId("p5"));
        Assert.Equal(5, first.SlotOf(new PrefixId("extra")));
    }

    private static byte[] Arp(Ipv4Address sender, Ipv4Address target)
    {
        byte[] frame = PacketBuilder.ArpReply(NetworkRig.GuestMac, sender, MacAddress.Broadcast, target);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(EthernetFrame.HeaderLength + 6), ArpPacket.Request);
        return frame;
    }

    private static byte[] ArpReply(MacAddress mac, Ipv4Address sender, MacAddress targetMac, Ipv4Address target) =>
        PacketBuilder.ArpReply(mac, sender, targetMac, target);

    private static byte[] Ping(Ipv4Address source, Ipv4Address destination)
    {
        byte[] request = [8, 0, 0, 0, 0, 1, 0, 1, 0x61, 0x62];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), PacketBuilder.Checksum(request, 0));
        byte[] frame = PacketBuilder.IcmpEchoReply(NetworkRig.GuestMac, AddressPlan.GatewayMac, source, destination, request);
        Span<byte> icmp = frame.AsSpan(EthernetFrame.HeaderLength + 20);
        icmp[0] = 8; // back to a request
        icmp[2] = 0;
        icmp[3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], PacketBuilder.Checksum(icmp, 0));
        return frame;
    }
}
