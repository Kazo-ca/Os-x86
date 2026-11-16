using System.Threading.Channels;

namespace OsX86.Network;

/// <summary>The four numbers and the VM that name a guest's TCP connection at the gateway.</summary>
/// <param name="VmSlot">The VM the frames come from.</param>
/// <param name="GuestIp">The guest's address (a VM or a prefix).</param>
/// <param name="GuestPort">The guest's port.</param>
/// <param name="RemoteIp">The destination.</param>
/// <param name="RemotePort">The destination port.</param>
internal readonly record struct FlowKey(int VmSlot, Ipv4Address GuestIp, ushort GuestPort, Ipv4Address RemoteIp, ushort RemotePort);

/// <summary>The state of a flow at the gateway.</summary>
internal enum FlowState
{
    /// <summary>The guest's SYN waits for the decision and the relay (its retransmissions are ignored).</summary>
    Deciding,

    /// <summary>The SYN-ACK went out; bytes flow both ways.</summary>
    Established,

    /// <summary>Over: removed from the table.</summary>
    Closed,
}

/// <summary>
/// One guest TCP connection ended at the gateway (the relay of Kazo speaks streams, not segments:
/// trial E6 ended TCP the same way in its relay). Minimal and strict: no window scaling, no SACK, no
/// timestamps are offered, so the guest uses none; segments out of order are dropped and the guest
/// retransmits them; what the relay sends is retransmitted from the first unacknowledged byte after
/// a timeout (go-back-N). Every member is used under the lock of the user's network.
/// </summary>
internal sealed class TcpFlow
{
    /// <summary>The window the gateway offers to the guest.</summary>
    public const int ReceiveWindow = 65535;

    /// <summary>The largest segment the gateway sends (1500 bytes of IP, no options).</summary>
    public const int MaxSegment = 1460;

    /// <summary>Bytes from the relay kept for the guest before the relay is read again.</summary>
    public const int SendBufferLimit = 256 * 1024;

    public static readonly TimeSpan InitialRetransmit = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan MaxRetransmit = TimeSpan.FromSeconds(8);

    public TcpFlow(FlowKey key, MacAddress guestMac, FlowIdentity identity, string vmName, uint guestIsn, ushort guestWindow, ushort? guestMss, uint iss, DateTimeOffset now)
    {
        Key = key;
        GuestMac = guestMac;
        Identity = identity;
        VmName = vmName;
        GuestIsn = guestIsn;
        RcvNext = guestIsn + 1;
        GuestWindow = guestWindow;
        Mss = Math.Clamp((int)(guestMss ?? 536), 64, MaxSegment);
        Iss = iss;
        SndUna = iss;
        SndNxt = iss + 1; // the SYN-ACK takes one number
        Started = now;
        LastActivity = now;
        Retransmit = InitialRetransmit;
    }

    public FlowKey Key { get; }

    public MacAddress GuestMac { get; }

    public FlowIdentity Identity { get; }

    public string VmName { get; }

    public string Host => Key.RemoteIp.ToString();

    public int Port => Key.RemotePort;

    public FlowState State { get; set; } = FlowState.Deciding;

    public uint GuestIsn { get; }

    /// <summary>The next byte expected from the guest.</summary>
    public uint RcvNext { get; set; }

    public ushort GuestWindow { get; set; }

    public int Mss { get; }

    public uint Iss { get; }

    /// <summary>The first byte sent to the guest and not acknowledged.</summary>
    public uint SndUna { get; set; }

    /// <summary>The next sequence number to send.</summary>
    public uint SndNxt { get; set; }

    /// <summary>What the relay sent and the guest has not acknowledged, from <see cref="SndUna"/> (after the SYN).</summary>
    public ByteQueue Outgoing { get; } = new();

    public bool RelayEnded { get; set; }

    public bool FinSent { get; set; }

    public bool FinAcked { get; set; }

    public bool GuestFinished { get; set; }

    public long BytesFromGuest { get; set; }

    public long BytesToGuest { get; set; }

    public int PendingToRelay { get; set; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset LastActivity { get; set; }

    public DateTimeOffset LastTransmit { get; set; }

    public TimeSpan Retransmit { get; set; }

    public int? RuleId { get; set; }

    public string DecisionReason { get; set; } = NetworkJournalReasons.Rule;

    public IRelayStream? Relay { get; set; }

    /// <summary>Bytes for the relay, in order; an empty array is the guest's FIN.</summary>
    public Channel<byte[]> ToRelay { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>Completed when the relay may be read again (the guest acknowledged enough).</summary>
    public TaskCompletionSource ReadAllowed { get; set; } = NewGate(open: true);

    public CancellationTokenSource Stop { get; } = new();

    /// <summary>The window the gateway advertises now: what the relay has not taken yet is deducted.</summary>
    public ushort AdvertisedWindow => (ushort)Math.Clamp(ReceiveWindow - PendingToRelay, 0, ReceiveWindow);

    /// <summary>The offset in <see cref="Outgoing"/> of the next byte never sent.</summary>
    public int Unsent => (int)(SndNxt - SndUna) - (FinSent && !FinAcked ? 1 : 0) - (SndUna == Iss ? 1 : 0);

    public static TaskCompletionSource NewGate(bool open)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (open)
        {
            gate.SetResult();
        }

        return gate;
    }

    /// <summary>Whether <paramref name="a"/> comes before <paramref name="b"/> in sequence space.</summary>
    public static bool Before(uint a, uint b) => (int)(a - b) < 0;

    /// <summary>The ends of a segment from the gateway to the guest.</summary>
    public TcpEndpoints Endpoints => new(AddressPlan.GatewayMac, GuestMac, Key.RemoteIp, Key.GuestIp, Key.RemotePort, Key.GuestPort);

    /// <summary>The SYN-ACK that accepts the connection.</summary>
    public byte[] SynAck() => PacketBuilder.Tcp(Endpoints, Iss, RcvNext, TcpFlags.Syn | TcpFlags.Ack, AdvertisedWindow, [], MaxSegment);

    /// <summary>A pure acknowledgment.</summary>
    public byte[] Ack() => PacketBuilder.Tcp(Endpoints, SndNxt, RcvNext, TcpFlags.Ack, AdvertisedWindow, []);

    /// <summary>A reset of the connection.</summary>
    public byte[] Reset() => PacketBuilder.Tcp(Endpoints, SndNxt, RcvNext, TcpFlags.Rst | TcpFlags.Ack, 0, []);

    /// <summary>
    /// The segments that may go now: the bytes of <see cref="Outgoing"/> not sent yet, within the
    /// guest's window, then the FIN once the relay ended and everything went.
    /// </summary>
    public List<byte[]> TakeSegments(DateTimeOffset now, bool probe = false)
    {
        var segments = new List<byte[]>();
        if (State != FlowState.Established)
        {
            return segments;
        }

        while (true)
        {
            int unsent = Unsent;
            int available = Outgoing.Count - unsent;
            int inFlight = (int)(SndNxt - SndUna);
            int window = GuestWindow - inFlight;
            if (probe && window <= 0 && inFlight == 0 && available > 0)
            {
                window = 1; // a zero window is probed with one byte
            }

            if (available <= 0 || window <= 0)
            {
                break;
            }

            int count = Math.Min(Math.Min(available, window), Mss);
            byte[] payload = Outgoing.Copy(unsent, count);
            segments.Add(PacketBuilder.Tcp(Endpoints, SndNxt, RcvNext, TcpFlags.Ack | TcpFlags.Psh, AdvertisedWindow, payload));
            SndNxt += (uint)count;
            probe = false;
        }

        if (RelayEnded && !FinSent && Unsent == Outgoing.Count)
        {
            segments.Add(PacketBuilder.Tcp(Endpoints, SndNxt, RcvNext, TcpFlags.Fin | TcpFlags.Ack, AdvertisedWindow, []));
            SndNxt++;
            FinSent = true;
        }

        if (segments.Count > 0)
        {
            LastTransmit = now;
        }

        return segments;
    }

    /// <summary>
    /// Takes an acknowledgment of the guest: the bytes it covers leave <see cref="Outgoing"/>.
    /// Returns whether it moved <see cref="SndUna"/>.
    /// </summary>
    public bool Acknowledge(uint ack, ushort window)
    {
        GuestWindow = window;
        if (!Before(SndUna, ack) || Before(SndNxt, ack))
        {
            return false;
        }

        int acknowledged = (int)(ack - SndUna);
        if (SndUna == Iss)
        {
            acknowledged--; // the SYN
        }

        if (FinSent && ack == SndNxt)
        {
            FinAcked = true;
            acknowledged--;
        }

        Outgoing.Consume(Math.Clamp(acknowledged, 0, Outgoing.Count));
        SndUna = ack;
        Retransmit = InitialRetransmit;
        return true;
    }

    /// <summary>Goes back to the first unacknowledged byte (a retransmission timeout): what follows is sent again.</summary>
    public void Rewind()
    {
        if (FinSent && !FinAcked)
        {
            FinSent = false;
        }

        SndNxt = SndUna == Iss ? Iss + 1 : SndUna;
        Retransmit = Retransmit * 2 > MaxRetransmit ? MaxRetransmit : Retransmit * 2;
    }
}

/// <summary>A queue of bytes: appended at the end, consumed from the start, copied from any offset.</summary>
internal sealed class ByteQueue
{
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;

    public int Count { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_start + Count + bytes.Length > _buffer.Length)
        {
            int needed = Count + bytes.Length;
            byte[] next = needed > _buffer.Length ? new byte[Math.Max(needed, _buffer.Length * 2)] : _buffer;
            Buffer.BlockCopy(_buffer, _start, next, 0, Count);
            _buffer = next;
            _start = 0;
        }

        bytes.CopyTo(_buffer.AsSpan(_start + Count));
        Count += bytes.Length;
    }

    public void Consume(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);
        _start += count;
        Count -= count;
        if (Count == 0)
        {
            _start = 0;
        }
    }

    public byte[] Copy(int offset, int count) => _buffer.AsSpan(_start + offset, count).ToArray();
}
