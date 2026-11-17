using System.Threading.Channels;
using OsX86.Network;
using OsX86.Prefixes;

namespace OsX86.Tests.Network;

/// <summary>A port of the switch that keeps what it gets, for the tests to read in order.</summary>
internal sealed class RecordingPort : INetworkPort
{
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();

    public void Deliver(byte[] frame) => _frames.Writer.TryWrite(frame);

    /// <summary>The next frame, within <paramref name="timeout"/> (default 10 s).</summary>
    public async Task<byte[]> NextAsync(TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await _frames.Reader.ReadAsync(deadline.Token);
    }

    /// <summary>The next frame that is a TCP segment, as its parts.</summary>
    public async Task<SegmentSeen> NextSegmentAsync(TimeSpan? timeout = null)
    {
        while (true)
        {
            byte[] frame = await NextAsync(timeout);
            if (SegmentSeen.TryRead(frame, out SegmentSeen? seen))
            {
                return seen!;
            }
        }
    }

    /// <summary>Whether a frame arrives within <paramref name="wait"/>.</summary>
    public async Task<bool> AnyWithinAsync(TimeSpan wait)
    {
        try
        {
            await NextAsync(wait);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>A TCP segment the gateway sent, copied out of its frame.</summary>
internal sealed record SegmentSeen(MacAddress SourceMac, MacAddress DestinationMac, Ipv4Address Source, Ipv4Address Destination, ushort SourcePort, ushort DestinationPort, uint Sequence, uint Acknowledgment, TcpFlags Flags, ushort Window, ushort? Mss, byte[] Payload, bool ChecksumOk)
{
    public bool Has(TcpFlags flag) => (Flags & flag) == flag;

    public static bool TryRead(byte[] frame, out SegmentSeen? seen)
    {
        seen = null;
        if (!EthernetFrame.TryRead(frame, out EthernetFrame ethernet) || ethernet.Type != EtherTypes.Ipv4
            || !Ipv4Packet.TryRead(ethernet.Payload, out Ipv4Packet ip) || ip.Protocol != IpProtocols.Tcp
            || !TcpSegment.TryRead(ip.Payload, out TcpSegment tcp))
        {
            return false;
        }

        bool checksum = PacketBuilder.TransportChecksum(ip.Source, ip.Destination, IpProtocols.Tcp, ip.Payload) == 0;
        seen = new SegmentSeen(ethernet.Source, ethernet.Destination, ip.Source, ip.Destination, tcp.SourcePort, tcp.DestinationPort, tcp.Sequence, tcp.Acknowledgment, tcp.Flags, tcp.Window, tcp.MaxSegmentSize, tcp.Payload.ToArray(), checksum);
        return true;
    }
}

/// <summary>A guest program's TCP side, written by hand: SYN, data, ACK, FIN, as a guest kernel would.</summary>
internal sealed class GuestTcp(VmNetworkAttachment vm, RecordingPort port, MacAddress mac, Ipv4Address address, ushort localPort, Ipv4Address remote, ushort remotePort)
{
    public uint Sequence { get; private set; } = 1000;

    public uint Acknowledged { get; private set; }

    public List<byte> Received { get; } = [];

    private TcpEndpoints Endpoints => new(mac, AddressPlan.GatewayMac, address, remote, localPort, remotePort);

    public void Send(TcpFlags flags, byte[]? payload = null, ushort window = 65535, ushort? mss = null, uint? sequence = null) =>
        vm.Receive(PacketBuilder.Tcp(Endpoints, sequence ?? Sequence, Acknowledged, flags, window, payload ?? [], mss));

    /// <summary>Sends the SYN and waits for the answer (a SYN-ACK or a reset).</summary>
    public async Task<SegmentSeen> OpenAsync(ushort mss = 1460, ushort window = 65535)
    {
        Send(TcpFlags.Syn, mss: mss, window: window);
        SegmentSeen answer = await port.NextSegmentAsync(TimeSpan.FromSeconds(20));
        if (answer.Has(TcpFlags.Syn | TcpFlags.Ack))
        {
            Sequence++;
            Acknowledged = answer.Sequence + 1;
            Send(TcpFlags.Ack, window: window);
        }

        return answer;
    }

    /// <summary>Sends bytes; the gateway's acknowledgment is read by the caller.</summary>
    public void Write(byte[] data)
    {
        Send(TcpFlags.Ack | TcpFlags.Psh, data);
        Sequence += (uint)data.Length;
    }

    /// <summary>Reads segments, acknowledges the data, until the gateway's FIN (or a reset).</summary>
    public async Task<SegmentSeen> ReadToEndAsync()
    {
        while (true)
        {
            SegmentSeen segment = await port.NextSegmentAsync(TimeSpan.FromSeconds(20));
            if (segment.Has(TcpFlags.Rst))
            {
                return segment;
            }

            if (segment.Payload.Length > 0 && segment.Sequence == Acknowledged)
            {
                Received.AddRange(segment.Payload);
                Acknowledged += (uint)segment.Payload.Length;
            }

            if (segment.Has(TcpFlags.Fin) && segment.Sequence + (uint)segment.Payload.Length == Acknowledged)
            {
                Acknowledged++;
                Send(TcpFlags.Ack);
                return segment;
            }

            if (segment.Payload.Length > 0)
            {
                Send(TcpFlags.Ack);
            }
        }
    }

    /// <summary>Sends the guest's FIN.</summary>
    public void Finish()
    {
        Send(TcpFlags.Fin | TcpFlags.Ack);
        Sequence++;
    }
}

/// <summary>A user network with one VM plugged in, a test relay and an injected window.</summary>
internal sealed class NetworkRig : IAsyncDisposable
{
    public static readonly Guid User = Guid.Parse("0000000000000000000000000000c001");
    public static readonly Guid Vm = Guid.Parse("11111111111111111111111111111111");
    public static readonly Ipv4Address Remote = Ipv4Address.Of(203, 0, 113, 9);
    public static readonly MacAddress GuestMac = new(0x02_00_00_00_00_02UL);

    public NetworkRig(Func<NetworkPermissionRequest, NetworkPermissionAnswer?>? answer = null, NetworkLimits? limits = null, TimeProvider? time = null)
    {
        Relay = new LocalTestRelay().WithHttp("203.0.113.9", 80).WithHttp("203.0.113.9", 443);
        Prompt = new InjectedPermissionPrompt(answer ?? (_ => NetworkPermissionAnswer.AllowOnce));
        Hub = new NetworkHub(Store, Relay, Prompt, limits, time);
        Network = Hub.For(User);
        Attachment = Network.Attach(Vm, "base", Port);
    }

    public InMemoryNetworkStore Store { get; } = new();

    public LocalTestRelay Relay { get; }

    public InjectedPermissionPrompt Prompt { get; }

    public NetworkHub Hub { get; }

    public UserNetwork Network { get; }

    public RecordingPort Port { get; } = new();

    public VmNetworkAttachment Attachment { get; }

    /// <summary>A connection from the prefix <paramref name="prefix"/> (its address given now), or from the VM itself.</summary>
    public GuestTcp Tcp(PrefixId? prefix, ushort localPort = 40000, ushort remotePort = 80, Ipv4Address? remote = null)
    {
        Ipv4Address source = prefix is { } named ? Attachment.AddressOf(named) : Attachment.Address;
        return new GuestTcp(Attachment, Port, GuestMac, source, localPort, remote ?? Remote, remotePort);
    }

    /// <summary>Waits for the journal to hold <paramref name="count"/> entries.</summary>
    public async Task<IReadOnlyList<NetworkJournalEntry>> JournalAsync(int count)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            IReadOnlyList<NetworkJournalEntry> entries = await Network.Journal.ReadAsync(CancellationToken.None);
            if (entries.Count >= count)
            {
                return entries;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"The journal did not reach {count} entries.");
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}
