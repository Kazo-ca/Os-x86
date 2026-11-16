using System.Threading.Channels;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Network;
using OsX86.Prefixes;

namespace OsX86.Vm;

/// <content>
/// Work item 12: the VM on its user's private network (decision 161 A). The engine gets a network
/// card when the manager has a network and the VM is not a DOS one; once the agent answers, the VM
/// is plugged into the user's switch (a slot and its address) and the agent brings the guest's card
/// up; each prefix gets its network namespace and its own address the first time it is used in a
/// run (E6, 146). An image without the network operations (the Alpine test image, an agent of
/// protocol 2) runs without network: nothing fails because of it.
/// </content>
public sealed partial class VmInstance
{
    private readonly HashSet<PrefixId> _networkPrefixes = [];
    private VmNetworkAttachment? _network;
    private EngineNetworkPort? _networkPort;

    /// <summary>The VM's address on the private network while it runs and is plugged in, otherwise <see langword="null"/>.</summary>
    public Ipv4Address? NetworkAddress
    {
        get
        {
            lock (_lock)
            {
                return _network?.Address;
            }
        }
    }

    /// <summary>The address of a prefix whose namespace was made in this run, otherwise <see langword="null"/>.</summary>
    public Ipv4Address? NetworkAddressOf(PrefixId prefix)
    {
        VmNetworkAttachment? network;
        lock (_lock)
        {
            network = _networkPrefixes.Contains(prefix) ? _network : null;
        }

        return network?.AddressOf(prefix);
    }

    /// <summary>
    /// Runs a command in a prefix and waits for it (the prefix and its namespace made first, as for a
    /// program): what the guest tests of the network run as a prefix's user.
    /// </summary>
    internal async Task<ExecResult> ExecInPrefixAsync(PrefixId prefix, string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        AgentClient agent = RequireAgent();
        await EnsureGuestPrefixAsync(agent, prefix, CurrentRun(), cancellationToken).ConfigureAwait(false);
        return await AgentCallAsync(() => agent.ExecInPrefixAsync(new ExecOptions { Prefix = prefix.Value, Program = program, Arguments = arguments, Timeout = timeout }, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The start options with the network card, when this VM gets one.</summary>
    private EngineStartOptions WithNetworkCard(EngineStartOptions options) =>
        _manager.Network is not null && Record.Profile.Kind != Interface.VmKind.Dos ? options with { Network = true } : options;

    /// <summary>
    /// Plugs the running VM into its user's network and brings the guest's card up. Any failure
    /// leaves the VM without network (reported in the background), never stops its start.
    /// </summary>
    private async Task ConnectNetworkAsync(IEngine engine, CancellationToken cancellationToken)
    {
        AgentClient? agent;
        lock (_lock)
        {
            agent = _hasAgent == true ? _agent : null;
        }

        if (_manager.Network is not { } hub || engine is not INetworkEngine card || agent is not { HasNetworkOperations: true })
        {
            return;
        }

        var port = new EngineNetworkPort(card);
        VmNetworkAttachment attachment = hub.For(OwnerId).Attach(Id, Record.Name, port);
        port.Connect(attachment);
        lock (_lock)
        {
            _network = attachment;
            _networkPort = port;
        }

        try
        {
            await AgentCallAsync(() => agent.NetworkUpAsync(attachment.VmSlot, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await DisconnectNetworkAsync().ConfigureAwait(false);
            if (!IsMissingNetworkSupport(exception))
            {
                _manager.ReportBackgroundError(this, exception);
            }
        }
    }

    /// <summary>The network namespace of a prefix, made once per run (when the VM is on the network).</summary>
    private async Task EnsurePrefixNetworkAsync(AgentClient agent, PrefixId prefix, CancellationToken cancellationToken)
    {
        VmNetworkAttachment? network;
        lock (_lock)
        {
            network = _network;
            if (network is null || _networkPrefixes.Contains(prefix) || !agent.HasNetworkOperations)
            {
                return;
            }
        }

        try
        {
            int slot = network.SlotOf(prefix);
            await AgentCallAsync(() => agent.PrefixNetworkUpAsync(prefix.Value, network.VmSlot, slot, cancellationToken)).ConfigureAwait(false);
            lock (_lock)
            {
                if (_network == network)
                {
                    _networkPrefixes.Add(prefix);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The prefix runs without network (the guest's rules leave a user outside a namespace
            // no way out): the program still runs, the error is reported.
            network.Release(prefix);
            _manager.ReportBackgroundError(this, exception);
        }
    }

    /// <summary>Frees the address of a deleted prefix and ends its flows (its namespace goes with its user in the guest).</summary>
    private void ReleasePrefixNetwork(PrefixId prefix)
    {
        VmNetworkAttachment? network;
        lock (_lock)
        {
            network = _network;
            _networkPrefixes.Remove(prefix);
        }

        network?.Release(prefix);
    }

    /// <summary>Unplugs the VM (its flows end) and stops the card's pump.</summary>
    private async Task DisconnectNetworkAsync()
    {
        VmNetworkAttachment? network;
        EngineNetworkPort? port;
        lock (_lock)
        {
            network = _network;
            port = _networkPort;
            _network = null;
            _networkPort = null;
            _networkPrefixes.Clear();
        }

        if (network is not null)
        {
            await network.DisposeAsync().ConfigureAwait(false);
        }

        if (port is not null)
        {
            await port.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>An image or agent without the network operation: the VM simply has no network.</summary>
    private static bool IsMissingNetworkSupport(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AgentException { Code: AgentErrorCodes.NoNetworkSupport or AgentErrorCodes.UnknownOperation })
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The VM's card as a port of the switch: the frames for the guest are queued and posted to the
/// engine in batches, in order, off the switch's lock; the frames of the guest go to the switch.
/// </summary>
internal sealed class EngineNetworkPort : INetworkPort, IAsyncDisposable
{
    private const int MaxBatch = 64;
    private readonly INetworkEngine _engine;
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private VmNetworkAttachment? _attachment;

    public EngineNetworkPort(INetworkEngine engine)
    {
        _engine = engine;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Starts handing the guest's frames to the switch.</summary>
    public void Connect(VmNetworkAttachment attachment)
    {
        _attachment = attachment;
        _engine.FramesReceived += OnFrames;
    }

    public void Deliver(byte[] frame) => _queue.Writer.TryWrite(frame);

    private void OnFrames(object? sender, NetworkFramesEventArgs e)
    {
        VmNetworkAttachment? attachment = _attachment;
        if (attachment is null)
        {
            return;
        }

        foreach (byte[] frame in e.Frames)
        {
            attachment.Receive(frame);
        }
    }

    private async Task PumpAsync()
    {
        var batch = new List<byte[]>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatch && _queue.Reader.TryRead(out byte[]? frame))
                {
                    batch.Add(frame);
                }

                try
                {
                    await _engine.SendFramesAsync([.. batch], _stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is EngineStateException or ObjectDisposedException)
                {
                    // The guest is stopping: its frames are dropped, like a cable pulled out.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Unplugged.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.FramesReceived -= OnFrames;
        _attachment = null;
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _stop.Dispose();
    }
}
