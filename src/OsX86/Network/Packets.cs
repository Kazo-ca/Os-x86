using System.Buffers.Binary;

namespace OsX86.Network;

/// <summary>The Ethernet types the private network knows.</summary>
public static class EtherTypes
{
    /// <summary>IPv4.</summary>
    public const ushort Ipv4 = 0x0800;

    /// <summary>ARP.</summary>
    public const ushort Arp = 0x0806;

    /// <summary>IPv6: never carried past the switch's own segment (174 A).</summary>
    public const ushort Ipv6 = 0x86DD;
}

/// <summary>The IP protocols the gateway reads.</summary>
public static class IpProtocols
{
    /// <summary>ICMP.</summary>
    public const byte Icmp = 1;

    /// <summary>TCP.</summary>
    public const byte Tcp = 6;

    /// <summary>UDP.</summary>
    public const byte Udp = 17;
}

/// <summary>The flags of a TCP segment.</summary>
[Flags]
public enum TcpFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The sender has finished sending.</summary>
    Fin = 0x01,

    /// <summary>Synchronize the sequence numbers (opening).</summary>
    Syn = 0x02,

    /// <summary>Reset the connection.</summary>
    Rst = 0x04,

    /// <summary>Push.</summary>
    Psh = 0x08,

    /// <summary>The acknowledgment number is meaningful.</summary>
    Ack = 0x10,
}

/// <summary>An Ethernet frame read from a guest: the header fields and the payload (no copy).</summary>
public readonly ref struct EthernetFrame
{
    /// <summary>The smallest frame: the 14 bytes of the header.</summary>
    public const int HeaderLength = 14;

    /// <summary>The largest frame the private network carries (1500 bytes of payload, no tag).</summary>
    public const int MaxLength = 1514;

    private EthernetFrame(MacAddress destination, MacAddress source, ushort type, ReadOnlySpan<byte> payload)
    {
        Destination = destination;
        Source = source;
        Type = type;
        Payload = payload;
    }

    /// <summary>The destination address.</summary>
    public MacAddress Destination { get; }

    /// <summary>The source address.</summary>
    public MacAddress Source { get; }

    /// <summary>The Ethernet type.</summary>
    public ushort Type { get; }

    /// <summary>What follows the header.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>Reads a frame; false when it is shorter than a header or longer than the network carries.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out EthernetFrame frame)
    {
        frame = default;
        if (bytes.Length < HeaderLength || bytes.Length > MaxLength)
        {
            return false;
        }

        frame = new EthernetFrame(MacAddress.Read(bytes), MacAddress.Read(bytes[6..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]), bytes[HeaderLength..]);
        return true;
    }
}

/// <summary>An ARP packet for IPv4 over Ethernet.</summary>
/// <param name="Operation">1 a request, 2 a reply.</param>
/// <param name="SenderMac">Who sends.</param>
/// <param name="SenderIp">Its address.</param>
/// <param name="TargetMac">Who is asked (zero in a request).</param>
/// <param name="TargetIp">The address asked for.</param>
public readonly record struct ArpPacket(ushort Operation, MacAddress SenderMac, Ipv4Address SenderIp, MacAddress TargetMac, Ipv4Address TargetIp)
{
    /// <summary>A request.</summary>
    public const ushort Request = 1;

    /// <summary>A reply.</summary>
    public const ushort Reply = 2;

    /// <summary>Reads the ARP payload of a frame; false when it is not IPv4 over Ethernet.</summary>
    public static bool TryRead(ReadOnlySpan<byte> payload, out ArpPacket packet)
    {
        packet = default;
        if (payload.Length < 28 || BinaryPrimitives.ReadUInt16BigEndian(payload) != 1 || BinaryPrimitives.ReadUInt16BigEndian(payload[2..]) != EtherTypes.Ipv4
            || payload[4] != 6 || payload[5] != 4)
        {
            return false;
        }

        packet = new ArpPacket(BinaryPrimitives.ReadUInt16BigEndian(payload[6..]), MacAddress.Read(payload[8..]), Ipv4Address.Read(payload[14..]), MacAddress.Read(payload[18..]), Ipv4Address.Read(payload[24..]));
        return true;
    }
}

/// <summary>An IPv4 packet read from a frame (no copy). Fragments are not reassembled: the gateway drops them.</summary>
public readonly ref struct Ipv4Packet
{
    private Ipv4Packet(Ipv4Address source, Ipv4Address destination, byte protocol, bool fragment, ReadOnlySpan<byte> payload)
    {
        Source = source;
        Destination = destination;
        Protocol = protocol;
        IsFragment = fragment;
        Payload = payload;
    }

    /// <summary>The source address: the identity of the sender (E6).</summary>
    public Ipv4Address Source { get; }

    /// <summary>The destination address.</summary>
    public Ipv4Address Destination { get; }

    /// <summary>The protocol of the payload.</summary>
    public byte Protocol { get; }

    /// <summary>Whether it is a fragment (more fragments, or an offset).</summary>
    public bool IsFragment { get; }

    /// <summary>The payload, cut to the total length the header gives.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>Reads the IPv4 payload of a frame; false when the header is not valid.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out Ipv4Packet packet)
    {
        packet = default;
        if (bytes.Length < 20 || (bytes[0] >> 4) != 4)
        {
            return false;
        }

        int headerLength = (bytes[0] & 0x0F) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if (headerLength < 20 || totalLength < headerLength || totalLength > bytes.Length)
        {
            return false;
        }

        ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        bool isFragment = (fragment & 0x2000) != 0 || (fragment & 0x1FFF) != 0;
        packet = new Ipv4Packet(Ipv4Address.Read(bytes[12..]), Ipv4Address.Read(bytes[16..]), bytes[9], isFragment, bytes[headerLength..totalLength]);
        return true;
    }
}

/// <summary>A TCP segment read from an IPv4 packet (no copy).</summary>
public readonly ref struct TcpSegment
{
    private TcpSegment(ushort sourcePort, ushort destinationPort, uint sequence, uint acknowledgment, TcpFlags flags, ushort window, ushort? maxSegmentSize, ReadOnlySpan<byte> payload)
    {
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        Sequence = sequence;
        Acknowledgment = acknowledgment;
        Flags = flags;
        Window = window;
        MaxSegmentSize = maxSegmentSize;
        Payload = payload;
    }

    /// <summary>The source port.</summary>
    public ushort SourcePort { get; }

    /// <summary>The destination port.</summary>
    public ushort DestinationPort { get; }

    /// <summary>The sequence number.</summary>
    public uint Sequence { get; }

    /// <summary>The acknowledgment number.</summary>
    public uint Acknowledgment { get; }

    /// <summary>The flags.</summary>
    public TcpFlags Flags { get; }

    /// <summary>The receive window of the sender (never scaled: the gateway offers no scaling).</summary>
    public ushort Window { get; }

    /// <summary>The maximum segment size option of a SYN, when it has one.</summary>
    public ushort? MaxSegmentSize { get; }

    /// <summary>The data.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>Whether a flag is set.</summary>
    public bool Has(TcpFlags flag) => (Flags & flag) == flag;

    /// <summary>Reads the TCP payload of an IPv4 packet; false when the header is not valid.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out TcpSegment segment)
    {
        segment = default;
        if (bytes.Length < 20)
        {
            return false;
        }

        int dataOffset = (bytes[12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > bytes.Length)
        {
            return false;
        }

        segment = new TcpSegment(
            BinaryPrimitives.ReadUInt16BigEndian(bytes),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]),
            (TcpFlags)(bytes[13] & 0x1F),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[14..]),
            ReadMaxSegmentSize(bytes[20..dataOffset]),
            bytes[dataOffset..]);
        return true;
    }

    private static ushort? ReadMaxSegmentSize(ReadOnlySpan<byte> options)
    {
        int index = 0;
        while (index < options.Length)
        {
            byte kind = options[index];
            if (kind == 0)
            {
                break;
            }

            if (kind == 1)
            {
                index++;
                continue;
            }

            if (index + 1 >= options.Length || options[index + 1] < 2 || index + options[index + 1] > options.Length)
            {
                break;
            }

            if (kind == 2 && options[index + 1] == 4)
            {
                return BinaryPrimitives.ReadUInt16BigEndian(options[(index + 2)..]);
            }

            index += options[index + 1];
        }

        return null;
    }
}

/// <summary>A UDP datagram's ports, read from an IPv4 packet.</summary>
/// <param name="SourcePort">The source port.</param>
/// <param name="DestinationPort">The destination port.</param>
public readonly record struct UdpHeader(ushort SourcePort, ushort DestinationPort)
{
    /// <summary>Reads the ports; false when the datagram is shorter than its header.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out UdpHeader header)
    {
        header = default;
        if (bytes.Length < 8)
        {
            return false;
        }

        header = new UdpHeader(BinaryPrimitives.ReadUInt16BigEndian(bytes), BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]));
        return true;
    }
}

/// <summary>Builds the frames the gateway sends to a guest, with their checksums.</summary>
public static class PacketBuilder
{
    private const int IpHeaderLength = 20;
    private const int TcpHeaderLength = 20;
    private const byte DefaultTtl = 64;

    /// <summary>An ARP reply from <paramref name="senderMac"/>/<paramref name="senderIp"/> to the asker.</summary>
    public static byte[] ArpReply(MacAddress senderMac, Ipv4Address senderIp, MacAddress targetMac, Ipv4Address targetIp)
    {
        byte[] frame = new byte[EthernetFrame.HeaderLength + 28];
        Span<byte> span = frame;
        WriteEthernet(span, targetMac, senderMac, EtherTypes.Arp);
        Span<byte> arp = span[EthernetFrame.HeaderLength..];
        BinaryPrimitives.WriteUInt16BigEndian(arp, 1);
        BinaryPrimitives.WriteUInt16BigEndian(arp[2..], EtherTypes.Ipv4);
        arp[4] = 6;
        arp[5] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(arp[6..], ArpPacket.Reply);
        senderMac.Write(arp[8..]);
        senderIp.Write(arp[14..]);
        targetMac.Write(arp[18..]);
        targetIp.Write(arp[24..]);
        return frame;
    }

    /// <summary>A TCP segment in an IPv4 packet in an Ethernet frame.</summary>
    public static byte[] Tcp(in TcpEndpoints endpoints, uint sequence, uint acknowledgment, TcpFlags flags, ushort window, ReadOnlySpan<byte> payload, ushort? maxSegmentSize = null)
    {
        int optionsLength = maxSegmentSize is null ? 0 : 4;
        int tcpLength = TcpHeaderLength + optionsLength + payload.Length;
        byte[] frame = new byte[EthernetFrame.HeaderLength + IpHeaderLength + tcpLength];
        Span<byte> span = frame;
        WriteEthernet(span, endpoints.DestinationMac, endpoints.SourceMac, EtherTypes.Ipv4);
        Span<byte> ip = span[EthernetFrame.HeaderLength..];
        WriteIpv4Header(ip, IpProtocols.Tcp, IpHeaderLength + tcpLength, endpoints.SourceIp, endpoints.DestinationIp);
        Span<byte> tcp = ip[IpHeaderLength..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, endpoints.SourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], endpoints.DestinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], acknowledgment);
        tcp[12] = (byte)(((TcpHeaderLength + optionsLength) / 4) << 4);
        tcp[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], window);
        if (maxSegmentSize is ushort mss)
        {
            tcp[20] = 2;
            tcp[21] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(tcp[22..], mss);
        }

        payload.CopyTo(tcp[(TcpHeaderLength + optionsLength)..]);
        ushort checksum = TransportChecksum(endpoints.SourceIp, endpoints.DestinationIp, IpProtocols.Tcp, tcp);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], checksum);
        return frame;
    }

    /// <summary>The echo reply to an ICMP echo request (<paramref name="request"/> is the ICMP message).</summary>
    public static byte[] IcmpEchoReply(MacAddress sourceMac, MacAddress destinationMac, Ipv4Address sourceIp, Ipv4Address destinationIp, ReadOnlySpan<byte> request)
    {
        byte[] frame = new byte[EthernetFrame.HeaderLength + IpHeaderLength + request.Length];
        Span<byte> span = frame;
        WriteEthernet(span, destinationMac, sourceMac, EtherTypes.Ipv4);
        Span<byte> ip = span[EthernetFrame.HeaderLength..];
        WriteIpv4Header(ip, IpProtocols.Icmp, IpHeaderLength + request.Length, sourceIp, destinationIp);
        Span<byte> icmp = ip[IpHeaderLength..];
        request.CopyTo(icmp);
        icmp[0] = 0; // echo reply
        icmp[2] = 0;
        icmp[3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum(icmp, 0));
        return frame;
    }

    /// <summary>The Internet checksum (RFC 1071) of <paramref name="bytes"/>, starting from a partial sum.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> bytes, uint initial)
    {
        uint sum = initial;
        int index = 0;
        for (; index + 1 < bytes.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(bytes[index..]);
        }

        if (index < bytes.Length)
        {
            sum += (uint)(bytes[index] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>The checksum of a TCP or UDP message with its IPv4 pseudo-header (the checksum field must be zero).</summary>
    public static ushort TransportChecksum(Ipv4Address source, Ipv4Address destination, byte protocol, ReadOnlySpan<byte> message)
    {
        uint pseudo = (source.Value >> 16) + (source.Value & 0xFFFF) + (destination.Value >> 16) + (destination.Value & 0xFFFF) + protocol + (uint)message.Length;
        return Checksum(message, pseudo);
    }

    private static void WriteEthernet(Span<byte> frame, MacAddress destination, MacAddress source, ushort type)
    {
        destination.Write(frame);
        source.Write(frame[6..]);
        BinaryPrimitives.WriteUInt16BigEndian(frame[12..], type);
    }

    private static void WriteIpv4Header(Span<byte> ip, byte protocol, int totalLength, Ipv4Address source, Ipv4Address destination)
    {
        ip[0] = 0x45;
        ip[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)totalLength);
        BinaryPrimitives.WriteUInt16BigEndian(ip[4..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0x4000); // don't fragment
        ip[8] = DefaultTtl;
        ip[9] = protocol;
        ip[10] = 0;
        ip[11] = 0;
        source.Write(ip[12..]);
        destination.Write(ip[16..]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip[..IpHeaderLength], 0));
    }
}

/// <summary>The two ends of a TCP segment the gateway sends.</summary>
/// <param name="SourceMac">The gateway's address.</param>
/// <param name="DestinationMac">The guest's address on the switch.</param>
/// <param name="SourceIp">The remote address the guest connected to.</param>
/// <param name="DestinationIp">The guest's address (a VM or a prefix).</param>
/// <param name="SourcePort">The remote port.</param>
/// <param name="DestinationPort">The guest's port.</param>
public readonly record struct TcpEndpoints(MacAddress SourceMac, MacAddress DestinationMac, Ipv4Address SourceIp, Ipv4Address DestinationIp, ushort SourcePort, ushort DestinationPort);
