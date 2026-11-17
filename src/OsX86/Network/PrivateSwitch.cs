namespace OsX86.Network;

/// <summary>Where the switch delivers the frames of one VM (its network card).</summary>
public interface INetworkPort
{
    /// <summary>Hands a frame to the VM; never blocks (the port queues it).</summary>
    void Deliver(byte[] frame);
}

/// <summary>Why the switch or the gateway dropped a frame (counted, for the tests and the measures).</summary>
public static class DropReasons
{
    /// <summary>Not a valid Ethernet, ARP or IPv4 frame.</summary>
    public const string Malformed = "malformed";

    /// <summary>A source address the VM does not own (another VM's, or outside the plan).</summary>
    public const string Spoofed = "spoofed";

    /// <summary>IPv6: never carried (174 A).</summary>
    public const string Ipv6 = "ipv6";

    /// <summary>An Ethernet type the private network does not carry.</summary>
    public const string OtherType = "other-type";

    /// <summary>A frame between VMs that is not VM to VM (a prefix never reaches another VM, 146).</summary>
    public const string Isolation = "isolation";

    /// <summary>A unicast frame to an address no port has.</summary>
    public const string UnknownDestination = "unknown-destination";

    /// <summary>A fragment: the gateway does not reassemble.</summary>
    public const string Fragment = "fragment";

    /// <summary>A packet to the private network the gateway does not serve.</summary>
    public const string Private = "private";

    /// <summary>A source address that names no VM or prefix of this run.</summary>
    public const string UnknownSource = "unknown-source";

    /// <summary>UDP: never carried out (174 A: DNS included).</summary>
    public const string Udp = "udp";

    /// <summary>A protocol the gateway does not carry (ICMP out, others).</summary>
    public const string Protocol = "protocol";
}

/// <summary>
/// The Ethernet switch of one user (decision 161 A): every running VM of the user is a port, the
/// gateway of Os-x86 is another. Frames go between the VMs of this user only (another user has
/// another switch). The switch checks every frame's source against the address plan, so a VM
/// never speaks with another VM's address, and carries only VM-to-VM traffic between VMs (a
/// prefix reaches the gateway alone). IPv6 is never carried. Used under the lock of the user's network.
/// </summary>
internal sealed class PrivateSwitch
{
    private readonly Dictionary<int, INetworkPort> _ports = [];
    private readonly Dictionary<MacAddress, int> _macs = [];
    private readonly Dictionary<int, MacAddress> _slotMacs = [];
    private readonly Dictionary<string, long> _drops = new(StringComparer.Ordinal);

    public PrivateSwitch(NetworkGateway gateway)
    {
        Gateway = gateway;
    }

    public NetworkGateway Gateway { get; }

    /// <summary>The frames dropped, by reason.</summary>
    public IReadOnlyDictionary<string, long> Drops => _drops;

    public void Attach(int vmSlot, INetworkPort port) => _ports[vmSlot] = port;

    public void Detach(int vmSlot)
    {
        _ports.Remove(vmSlot);
        _slotMacs.Remove(vmSlot);
        foreach (MacAddress mac in _macs.Where(pair => pair.Value == vmSlot).Select(pair => pair.Key).ToList())
        {
            _macs.Remove(mac);
        }
    }

    public void Drop(string reason) => _drops[reason] = _drops.GetValueOrDefault(reason) + 1;

    /// <summary>Delivers a frame to the VM of a slot, when it is attached.</summary>
    public void Deliver(int vmSlot, byte[] frame)
    {
        if (_ports.TryGetValue(vmSlot, out INetworkPort? port))
        {
            port.Deliver(frame);
        }
    }

    /// <summary>A frame from the VM in <paramref name="vmSlot"/>.</summary>
    public void Receive(int vmSlot, ReadOnlySpan<byte> bytes)
    {
        if (!_ports.ContainsKey(vmSlot))
        {
            return;
        }

        if (!EthernetFrame.TryRead(bytes, out EthernetFrame frame) || frame.Source.IsGroup)
        {
            Drop(DropReasons.Malformed);
            return;
        }

        // A VM has one Ethernet address, the first its card uses (its kernel's, before anything the
        // guest could choose), and an address belongs to one VM until that VM leaves: a VM can
        // neither take another's address nor cut it off by sending from it.
        if ((_macs.TryGetValue(frame.Source, out int owner) && owner != vmSlot)
            || (_slotMacs.TryGetValue(vmSlot, out MacAddress own) && own != frame.Source))
        {
            Drop(DropReasons.Spoofed);
            return;
        }

        switch (frame.Type)
        {
            case EtherTypes.Arp:
                ReceiveArp(vmSlot, frame, bytes);
                break;
            case EtherTypes.Ipv4:
                ReceiveIpv4(vmSlot, frame, bytes);
                break;
            case EtherTypes.Ipv6:
                Drop(DropReasons.Ipv6);
                break;
            default:
                Drop(DropReasons.OtherType);
                break;
        }
    }

    private void ReceiveArp(int vmSlot, EthernetFrame frame, ReadOnlySpan<byte> bytes)
    {
        if (!ArpPacket.TryRead(frame.Payload, out ArpPacket arp) || arp.SenderMac != frame.Source)
        {
            Drop(DropReasons.Malformed);
            return;
        }

        if (arp.SenderIp.Value != 0 && !AddressPlan.MaySend(vmSlot, arp.SenderIp))
        {
            Drop(DropReasons.Spoofed);
            return;
        }

        Learn(vmSlot, frame.Source);
        if (arp.TargetIp == AddressPlan.Gateway)
        {
            Gateway.ReceiveArp(vmSlot, arp);
            return;
        }

        // Between VMs: only the VMs' own addresses resolve each other; a request goes to the one VM it asks for.
        if (arp.SenderIp != AddressPlan.VmAddress(vmSlot) || !TryVmSlotOf(arp.TargetIp, out int target) || target == vmSlot || !_ports.ContainsKey(target))
        {
            Drop(DropReasons.Isolation);
            return;
        }

        Deliver(target, bytes.ToArray());
    }

    private void ReceiveIpv4(int vmSlot, EthernetFrame frame, ReadOnlySpan<byte> bytes)
    {
        if (!Ipv4Packet.TryRead(frame.Payload, out Ipv4Packet packet))
        {
            Drop(DropReasons.Malformed);
            return;
        }

        if (!AddressPlan.MaySend(vmSlot, packet.Source))
        {
            Drop(DropReasons.Spoofed);
            return;
        }

        Learn(vmSlot, frame.Source);
        if (frame.Destination == AddressPlan.GatewayMac)
        {
            Gateway.ReceiveIpv4(vmSlot, frame.Source, packet);
            return;
        }

        if (frame.Destination.IsGroup)
        {
            Drop(DropReasons.OtherType);
            return;
        }

        if (!_macs.TryGetValue(frame.Destination, out int target) || target == vmSlot)
        {
            Drop(DropReasons.UnknownDestination);
            return;
        }

        if (packet.Source != AddressPlan.VmAddress(vmSlot) || packet.Destination != AddressPlan.VmAddress(target))
        {
            Drop(DropReasons.Isolation);
            return;
        }

        Deliver(target, bytes.ToArray());
    }

    private void Learn(int vmSlot, MacAddress mac)
    {
        _macs[mac] = vmSlot;
        _slotMacs[vmSlot] = mac;
    }

    private static bool TryVmSlotOf(Ipv4Address address, out int vmSlot) =>
        AddressPlan.TryResolve(address, out vmSlot, out int? prefixSlot) && prefixSlot is null;
}
