namespace OsX86.Network;

/// <summary>Who a source address is, for the gateway: the identity and the VM's name.</summary>
internal readonly record struct SourceIdentity(FlowIdentity Identity, string VmName);

/// <summary>What the gateway needs from the user's network.</summary>
internal interface IGatewayHost
{
    /// <summary>The lock every frame and every flow is handled under.</summary>
    Lock Sync { get; }

    /// <summary>Delivers a frame to a VM.</summary>
    void Deliver(int vmSlot, byte[] frame);

    /// <summary>Counts a dropped frame.</summary>
    void Drop(string reason);

    /// <summary>Who sends from <paramref name="source"/> in the VM of <paramref name="vmSlot"/>, if anyone this run.</summary>
    SourceIdentity? Resolve(int vmSlot, Ipv4Address source);

    /// <summary>Writes a journal entry (in order, in the background).</summary>
    void Journal(NetworkJournalEntry entry);

    /// <summary>Reports an error of a background task.</summary>
    void ReportError(Exception exception);
}

/// <summary>
/// The gateway of a user's private network (10.86.0.1): answers ARP and ping for its address, ends
/// every TCP connection a guest opens to an address outside the private network, decides it
/// (rules, then the permission window), holds the limits (ports, flows per identity and per user,
/// idle time), opens the flow through the relay with the identity of the source address, and
/// writes the journal. UDP (DNS included) and anything else never leave (174 A).
/// </summary>
internal sealed class NetworkGateway
{
    private static readonly TimeSpan UdpJournalInterval = TimeSpan.FromSeconds(60);
    private readonly IGatewayHost _host;
    private readonly NetworkPolicy _policy;
    private readonly IRelayConnector _relay;
    private readonly NetworkLimits _limits;
    private readonly TimeProvider _time;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<FlowKey, TcpFlow> _flows = [];
    private readonly Dictionary<(FlowIdentity, string, int), DateTimeOffset> _udpJournaled = [];
    private uint _nextIss = 0x4F58_0000;

    public NetworkGateway(IGatewayHost host, NetworkPolicy policy, IRelayConnector relay, NetworkLimits limits, TimeProvider time, CancellationToken lifetime)
    {
        _host = host;
        _policy = policy;
        _relay = relay;
        _limits = limits;
        _time = time;
        _lifetime = lifetime;
    }

    /// <summary>The flows at the gateway now (any state).</summary>
    public int FlowCount => _flows.Count;

    public void ReceiveArp(int vmSlot, ArpPacket arp)
    {
        if (arp.Operation == ArpPacket.Request)
        {
            _host.Deliver(vmSlot, PacketBuilder.ArpReply(AddressPlan.GatewayMac, AddressPlan.Gateway, arp.SenderMac, arp.SenderIp));
        }
    }

    public void ReceiveIpv4(int vmSlot, MacAddress sourceMac, Ipv4Packet packet)
    {
        if (packet.IsFragment)
        {
            _host.Drop(DropReasons.Fragment);
            return;
        }

        if (packet.Destination == AddressPlan.Gateway)
        {
            ReceiveForGateway(vmSlot, sourceMac, packet);
            return;
        }

        if (AddressPlan.IsPrivate(packet.Destination))
        {
            _host.Drop(DropReasons.Private);
            return;
        }

        if (_host.Resolve(vmSlot, packet.Source) is not SourceIdentity source)
        {
            _host.Drop(DropReasons.UnknownSource);
            return;
        }

        switch (packet.Protocol)
        {
            case IpProtocols.Tcp when TcpSegment.TryRead(packet.Payload, out TcpSegment segment):
                ReceiveTcp(new FlowKey(vmSlot, packet.Source, segment.SourcePort, packet.Destination, segment.DestinationPort), sourceMac, source, segment);
                break;
            case IpProtocols.Udp:
                RefuseUdp(source, packet);
                break;
            default:
                _host.Drop(DropReasons.Protocol);
                break;
        }
    }

    /// <summary>Retransmissions, zero-window probes and idle flows; called a few times a second.</summary>
    public void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (TcpFlow flow in _flows.Values.ToList())
        {
            if (flow.State != FlowState.Established)
            {
                continue;
            }

            if (now - flow.LastActivity >= _limits.IdleTimeout)
            {
                Abort(flow, NetworkJournalReasons.Idle, sendReset: true);
                continue;
            }

            if (now - flow.LastTransmit < flow.Retransmit)
            {
                continue;
            }

            if (flow.SndUna != flow.SndNxt)
            {
                bool synPending = flow.SndUna == flow.Iss;
                flow.Rewind();
                if (synPending)
                {
                    flow.LastTransmit = now;
                    Send(flow, flow.SynAck());
                    continue;
                }
            }

            SendAll(flow, flow.TakeSegments(now, probe: true));
        }
    }

    /// <summary>Ends every flow of a VM that stops (no reset: its card is gone).</summary>
    public void CloseVm(int vmSlot)
    {
        foreach (TcpFlow flow in _flows.Values.Where(flow => flow.Key.VmSlot == vmSlot).ToList())
        {
            Abort(flow, NetworkJournalReasons.VmStopped, sendReset: false);
        }
    }

    /// <summary>Ends every flow of an identity that goes (a deleted prefix).</summary>
    public void CloseIdentity(FlowIdentity identity)
    {
        foreach (TcpFlow flow in _flows.Values.Where(flow => flow.Identity == identity).ToList())
        {
            Abort(flow, NetworkJournalReasons.VmStopped, sendReset: true);
        }
    }

    private void ReceiveForGateway(int vmSlot, MacAddress sourceMac, Ipv4Packet packet)
    {
        // Ping of the gateway (a guest checks its link); nothing else is served at 10.86.0.1.
        if (packet.Protocol == IpProtocols.Icmp && packet.Payload.Length >= 8 && packet.Payload[0] == 8)
        {
            _host.Deliver(vmSlot, PacketBuilder.IcmpEchoReply(AddressPlan.GatewayMac, sourceMac, AddressPlan.Gateway, packet.Source, packet.Payload));
            return;
        }

        _host.Drop(DropReasons.Protocol);
    }

    private void RefuseUdp(SourceIdentity source, Ipv4Packet packet)
    {
        _host.Drop(DropReasons.Udp);
        if (!UdpHeader.TryRead(packet.Payload, out UdpHeader udp))
        {
            return;
        }

        // One journal entry per identity and destination per minute: a resolver retrying is one attempt.
        string host = packet.Destination.ToString();
        DateTimeOffset now = _time.GetUtcNow();
        if (_udpJournaled.TryGetValue((source.Identity, host, udp.DestinationPort), out DateTimeOffset last) && now - last < UdpJournalInterval)
        {
            return;
        }

        _udpJournaled[(source.Identity, host, udp.DestinationPort)] = now;
        _host.Journal(Entry(source.Identity, host, udp.DestinationPort, NetworkJournalEvent.Refused, NetworkJournalReasons.Udp) with { Detail = udp.DestinationPort == 53 ? "udp/53 (dns)" : "udp" });
    }

    private void ReceiveTcp(FlowKey key, MacAddress sourceMac, SourceIdentity source, TcpSegment segment)
    {
        if (!_flows.TryGetValue(key, out TcpFlow? flow))
        {
            if (segment.Has(TcpFlags.Syn) && !segment.Has(TcpFlags.Ack) && !segment.Has(TcpFlags.Rst))
            {
                Open(key, sourceMac, source, segment);
            }
            else if (!segment.Has(TcpFlags.Rst))
            {
                ResetUnknown(key, sourceMac, segment);
            }

            return;
        }

        if (flow.State == FlowState.Deciding)
        {
            return; // retransmitted SYNs while the decision is pending
        }

        HandleEstablished(flow, segment);
    }

    private void Open(FlowKey key, MacAddress sourceMac, SourceIdentity source, TcpSegment syn)
    {
        string host = key.RemoteIp.ToString();
        string? refusal = !_limits.AllowedPorts.Contains(key.RemotePort) ? NetworkJournalReasons.Port
            : _flows.Values.Count(flow => flow.Identity == source.Identity) >= _limits.MaxFlowsPerIdentity ? NetworkJournalReasons.FlowsPerIdentity
            : _flows.Count >= _limits.MaxFlowsPerUser ? NetworkJournalReasons.FlowsPerUser
            : null;
        if (refusal is not null)
        {
            _host.Deliver(key.VmSlot, RefuseSyn(key, sourceMac, syn));
            _host.Journal(Entry(source.Identity, host, key.RemotePort, NetworkJournalEvent.Refused, refusal));
            return;
        }

        _nextIss += 0x0001_0F31;
        var flow = new TcpFlow(key, sourceMac, source.Identity, source.VmName, syn.Sequence, syn.Window, syn.MaxSegmentSize, _nextIss, _time.GetUtcNow());
        _flows[key] = flow;
        uint synSequence = syn.Sequence;
        _ = Task.Run(() => DecideAndConnectAsync(flow, sourceMac, synSequence));
    }

    private async Task DecideAndConnectAsync(TcpFlow flow, MacAddress sourceMac, uint synSequence)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, flow.Stop.Token);
        FlowDecision decision;
        IRelayStream? relay = null;
        string? relayError = null;
        try
        {
            decision = await _policy.DecideAsync(flow.Identity, flow.VmName, flow.Host, flow.Port, linked.Token).ConfigureAwait(false);
            if (decision.Allowed)
            {
                try
                {
                    relay = await _relay.ConnectAsync(new RelayFlowRequest(flow.Identity, flow.Host, flow.Port), linked.Token).ConfigureAwait(false);
                }
                catch (RelayException exception)
                {
                    relayError = exception.Code;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException || linked.IsCancellationRequested)
        {
            return; // the VM or the network went away: the flow is already gone
        }
        catch (Exception exception)
        {
            _host.ReportError(exception);
            decision = new FlowDecision(false, NetworkJournalReasons.NoPrompt, null);
        }

        lock (_host.Sync)
        {
            bool live = _flows.TryGetValue(flow.Key, out TcpFlow? current) && current == flow && flow.State == FlowState.Deciding;
            if (!live)
            {
                _ = relay?.DisposeAsync().AsTask();
                return;
            }

            if (!decision.Allowed || relay is null)
            {
                _flows.Remove(flow.Key);
                flow.State = FlowState.Closed;
                _host.Deliver(flow.Key.VmSlot, PacketBuilder.Tcp(flow.Endpoints, 0, synSequence + 1, TcpFlags.Rst | TcpFlags.Ack, 0, []));
                _host.Journal(Entry(flow.Identity, flow.Host, flow.Port, NetworkJournalEvent.Refused, decision.Allowed ? NetworkJournalReasons.Relay : decision.Reason) with
                {
                    Rule = decision.RuleId,
                    Detail = relayError,
                });
                return;
            }

            flow.Relay = relay;
            flow.RuleId = decision.RuleId;
            flow.DecisionReason = decision.Reason;
            flow.State = FlowState.Established;
            flow.LastActivity = _time.GetUtcNow();
            flow.LastTransmit = flow.LastActivity;
            _host.Journal(Entry(flow.Identity, flow.Host, flow.Port, NetworkJournalEvent.Opened, decision.Reason) with { Rule = decision.RuleId });
            Send(flow, flow.SynAck());
        }

        _ = Task.Run(() => PumpFromRelayAsync(flow, relay));
        _ = Task.Run(() => PumpToRelayAsync(flow, relay));
    }

    private void HandleEstablished(TcpFlow flow, TcpSegment segment)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (segment.Has(TcpFlags.Rst))
        {
            Abort(flow, NetworkJournalReasons.Reset, sendReset: false);
            return;
        }

        if (segment.Has(TcpFlags.Syn))
        {
            if (segment.Sequence == flow.GuestIsn && flow.SndUna == flow.Iss)
            {
                Send(flow, flow.SynAck()); // our SYN-ACK was lost
            }

            return;
        }

        bool acknowledge = false;
        if (segment.Has(TcpFlags.Ack) && flow.Acknowledge(segment.Acknowledgment, segment.Window))
        {
            flow.LastActivity = now;
            if (flow.Outgoing.Count < TcpFlow.SendBufferLimit / 2)
            {
                flow.ReadAllowed.TrySetResult();
            }
        }

        ReadOnlySpan<byte> payload = segment.Payload;
        if (payload.Length > 0)
        {
            acknowledge = true;
            if (segment.Sequence == flow.RcvNext && !flow.GuestFinished)
            {
                flow.ToRelay.Writer.TryWrite(payload.ToArray());
                flow.PendingToRelay += payload.Length;
                flow.RcvNext += (uint)payload.Length;
                flow.BytesFromGuest += payload.Length;
                flow.LastActivity = now;
            }
        }

        if (segment.Has(TcpFlags.Fin) && !flow.GuestFinished && segment.Sequence + (uint)payload.Length == flow.RcvNext)
        {
            flow.GuestFinished = true;
            flow.RcvNext++;
            flow.ToRelay.Writer.TryWrite([]);
            flow.ToRelay.Writer.TryComplete();
            acknowledge = true;
        }

        List<byte[]> segments = flow.TakeSegments(now);
        if (segments.Count == 0 && acknowledge)
        {
            segments.Add(flow.Ack());
        }

        SendAll(flow, segments);
        if (flow.GuestFinished && flow.FinAcked)
        {
            Close(flow, NetworkJournalReasons.Ended);
        }
    }

    private async Task PumpFromRelayAsync(TcpFlow flow, IRelayStream relay)
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                Task allowed;
                lock (_host.Sync)
                {
                    if (flow.State != FlowState.Established)
                    {
                        return;
                    }

                    if (flow.Outgoing.Count >= TcpFlow.SendBufferLimit && flow.ReadAllowed.Task.IsCompleted)
                    {
                        flow.ReadAllowed = TcpFlow.NewGate(open: false);
                    }

                    allowed = flow.ReadAllowed.Task;
                }

                await allowed.WaitAsync(flow.Stop.Token).ConfigureAwait(false);
                int count = await relay.ReadAsync(buffer, flow.Stop.Token).ConfigureAwait(false);
                lock (_host.Sync)
                {
                    if (flow.State != FlowState.Established)
                    {
                        return;
                    }

                    DateTimeOffset now = _time.GetUtcNow();
                    flow.LastActivity = now;
                    if (count == 0)
                    {
                        flow.RelayEnded = true;
                    }
                    else
                    {
                        flow.Outgoing.Append(buffer.AsSpan(0, count));
                        flow.BytesToGuest += count;
                    }

                    SendAll(flow, flow.TakeSegments(now));
                    if (count == 0)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception) when (flow.Stop.IsCancellationRequested)
        {
            // The flow ended.
        }
        catch (Exception)
        {
            lock (_host.Sync)
            {
                Abort(flow, NetworkJournalReasons.Relay, sendReset: true);
            }
        }
    }

    private async Task PumpToRelayAsync(TcpFlow flow, IRelayStream relay)
    {
        try
        {
            await foreach (byte[] chunk in flow.ToRelay.Reader.ReadAllAsync(flow.Stop.Token).ConfigureAwait(false))
            {
                if (chunk.Length == 0)
                {
                    await relay.CompleteWritesAsync(flow.Stop.Token).ConfigureAwait(false);
                    return;
                }

                await relay.WriteAsync(chunk, flow.Stop.Token).ConfigureAwait(false);
                lock (_host.Sync)
                {
                    bool wasClosed = flow.AdvertisedWindow < TcpFlow.MaxSegment;
                    flow.PendingToRelay -= chunk.Length;
                    if (wasClosed && flow.AdvertisedWindow >= TcpFlow.MaxSegment && flow.State == FlowState.Established)
                    {
                        Send(flow, flow.Ack()); // the window opens again
                    }
                }
            }
        }
        catch (Exception) when (flow.Stop.IsCancellationRequested)
        {
            // The flow ended.
        }
        catch (Exception)
        {
            lock (_host.Sync)
            {
                Abort(flow, NetworkJournalReasons.Relay, sendReset: true);
            }
        }
    }

    private void Abort(TcpFlow flow, string reason, bool sendReset)
    {
        if (flow.State == FlowState.Established && sendReset)
        {
            Send(flow, flow.Reset());
        }

        Close(flow, reason);
    }

    private void Close(TcpFlow flow, string reason)
    {
        if (flow.State == FlowState.Closed)
        {
            return;
        }

        bool opened = flow.State == FlowState.Established;
        flow.State = FlowState.Closed;
        _flows.Remove(flow.Key);
        flow.ReadAllowed.TrySetResult();
        flow.ToRelay.Writer.TryComplete();
        flow.Stop.Cancel();
        if (flow.Relay is { } relay)
        {
            _ = relay.DisposeAsync().AsTask();
        }

        if (opened)
        {
            _host.Journal(Entry(flow.Identity, flow.Host, flow.Port, NetworkJournalEvent.Closed, reason) with
            {
                Rule = flow.RuleId,
                BytesOut = flow.BytesFromGuest,
                BytesIn = flow.BytesToGuest,
                DurationMs = (long)(_time.GetUtcNow() - flow.Started).TotalMilliseconds,
            });
        }
    }

    private void ResetUnknown(FlowKey key, MacAddress sourceMac, TcpSegment segment)
    {
        var endpoints = new TcpEndpoints(AddressPlan.GatewayMac, sourceMac, key.RemoteIp, key.GuestIp, key.RemotePort, key.GuestPort);
        byte[] reset = segment.Has(TcpFlags.Ack)
            ? PacketBuilder.Tcp(endpoints, segment.Acknowledgment, 0, TcpFlags.Rst, 0, [])
            : PacketBuilder.Tcp(endpoints, 0, segment.Sequence + (uint)segment.Payload.Length + (segment.Has(TcpFlags.Fin) ? 1u : 0u), TcpFlags.Rst | TcpFlags.Ack, 0, []);
        _host.Deliver(key.VmSlot, reset);
    }

    private static byte[] RefuseSyn(FlowKey key, MacAddress sourceMac, TcpSegment syn) =>
        PacketBuilder.Tcp(new TcpEndpoints(AddressPlan.GatewayMac, sourceMac, key.RemoteIp, key.GuestIp, key.RemotePort, key.GuestPort), 0, syn.Sequence + 1, TcpFlags.Rst | TcpFlags.Ack, 0, []);

    private void Send(TcpFlow flow, byte[] frame) => _host.Deliver(flow.Key.VmSlot, frame);

    private void SendAll(TcpFlow flow, List<byte[]> frames)
    {
        foreach (byte[] frame in frames)
        {
            Send(flow, frame);
        }
    }

    private static NetworkJournalEntry Entry(FlowIdentity identity, string host, int port, NetworkJournalEvent journalEvent, string reason) => new()
    {
        Time = default,
        Vm = identity.VmId,
        Prefix = identity.Prefix,
        Host = host,
        Port = port,
        Event = journalEvent,
        Reason = reason,
    };
}
