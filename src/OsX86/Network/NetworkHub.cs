using System.Threading.Channels;
using OsX86.Interface;
using OsX86.Prefixes;

namespace OsX86.Network;

/// <summary>
/// The private networks of every user of the machine (plan §7: <c>Network/</c>): one switch,
/// gateway, rule book, journal and policy per user, created on first use; the relay and the
/// permission window are shared (each request names its user). One hub per VM manager.
/// </summary>
public sealed class NetworkHub : IAsyncDisposable
{
    private readonly Dictionary<Guid, UserNetwork> _users = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    /// <summary>Creates the hub.</summary>
    /// <param name="store">Where the rules and the journals are kept.</param>
    /// <param name="relay">The way out (the relay of Kazo when ADR 0255 is coded; <see cref="UnavailableRelay"/> until then).</param>
    /// <param name="prompt">The permission window (<see cref="UnavailablePermissionPrompt"/> until ADR 0246 is in a released Kazo).</param>
    /// <param name="limits">The limits of every flow.</param>
    /// <param name="time">The clock, replaced by the tests.</param>
    public NetworkHub(INetworkStore store, IRelayConnector relay, INetworkPermissionPrompt prompt, NetworkLimits? limits = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(prompt);
        Store = store;
        Relay = relay;
        Prompt = prompt;
        Limits = limits ?? new NetworkLimits();
        Limits.Validate();
        Time = time ?? TimeProvider.System;
    }

    /// <summary>A hub without relay nor window: rules and journal work, every allowed flow fails at the relay.</summary>
    public static NetworkHub Unavailable() => new(new InMemoryNetworkStore(), UnavailableRelay.Instance, UnavailablePermissionPrompt.Instance);

    /// <summary>Where the rules and journals are kept.</summary>
    public INetworkStore Store { get; }

    /// <summary>The relay.</summary>
    public IRelayConnector Relay { get; }

    /// <summary>The permission window.</summary>
    public INetworkPermissionPrompt Prompt { get; }

    /// <summary>The limits.</summary>
    public NetworkLimits Limits { get; }

    /// <summary>The clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>Raised for an error of a background task of a network (a journal that could not be written).</summary>
    public event EventHandler<Exception>? BackgroundError;

    /// <summary>The network of a user, created on first use.</summary>
    public UserNetwork For(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A network belongs to a user.", nameof(userId));
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_users.TryGetValue(userId, out UserNetwork? network))
            {
                network = new UserNetwork(userId, this, _lifetime.Token);
                _users[userId] = network;
            }

            return network;
        }
    }

    internal void ReportError(Exception exception) => BackgroundError?.Invoke(this, exception);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<UserNetwork> networks;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            networks = [.. _users.Values];
            _users.Clear();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (UserNetwork network in networks)
        {
            await network.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}

/// <summary>
/// The private network of one user: the switch its running VMs are plugged into, the gateway, the
/// slots of the address plan, the rules, the journal and the policy.
/// </summary>
public sealed class UserNetwork : IGatewayHost, IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private readonly NetworkHub _hub;
    private readonly PrivateSwitch _switch;
    private readonly NetworkGateway _gateway;
    private readonly Dictionary<int, VmNetworkAttachment> _vms = [];
    private readonly Channel<NetworkJournalEntry> _journalQueue = Channel.CreateUnbounded<NetworkJournalEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _journalWriter;
    private readonly ITimer _ticker;

    internal UserNetwork(Guid userId, NetworkHub hub, CancellationToken lifetime)
    {
        UserId = userId;
        _hub = hub;
        Rules = new NetworkRuleBook(userId, hub.Store, hub.Time);
        Journal = new NetworkJournal(userId, hub.Store, hub.Time);
        Policy = new NetworkPolicy(Rules, hub.Prompt);
        _gateway = new NetworkGateway(this, Policy, hub.Relay, hub.Limits, hub.Time, lifetime);
        _switch = new PrivateSwitch(_gateway);
        _journalWriter = Task.Run(WriteJournalAsync);
        _ticker = hub.Time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
    }

    /// <summary>The user.</summary>
    public Guid UserId { get; }

    /// <summary>The user's rules.</summary>
    public NetworkRuleBook Rules { get; }

    /// <summary>The user's journal.</summary>
    public NetworkJournal Journal { get; }

    /// <summary>The user's policy.</summary>
    public NetworkPolicy Policy { get; }

    /// <summary>The lock of every frame and flow of this network.</summary>
    public Lock Sync { get; } = new();

    /// <summary>The frames dropped so far, by reason (<see cref="DropReasons"/>).</summary>
    public IReadOnlyDictionary<string, long> Drops
    {
        get
        {
            lock (Sync)
            {
                return new Dictionary<string, long>(_switch.Drops, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The flows at the gateway now.</summary>
    public int FlowCount
    {
        get
        {
            lock (Sync)
            {
                return _gateway.FlowCount;
            }
        }
    }

    /// <summary>The VMs plugged in now.</summary>
    public IReadOnlyList<VmNetworkAttachment> Attached
    {
        get
        {
            lock (Sync)
            {
                return [.. _vms.Values.OrderBy(vm => vm.VmSlot)];
            }
        }
    }

    /// <summary>Plugs a VM in: it gets the lowest free slot of the plan, its address, its port on the switch.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.NetworkUnavailable"/> when every slot is taken.</exception>
    public VmNetworkAttachment Attach(Guid vmId, string vmName, INetworkPort port)
    {
        ArgumentException.ThrowIfNullOrEmpty(vmName);
        ArgumentNullException.ThrowIfNull(port);
        lock (Sync)
        {
            if (_vms.Values.Any(vm => vm.VmId == vmId))
            {
                throw new InvalidOperationException($"The VM {vmId} is already on the network.");
            }

            int slot = Enumerable.Range(AddressPlan.FirstVmSlot, AddressPlan.LastVmSlot - AddressPlan.FirstVmSlot + 1).FirstOrDefault(candidate => !_vms.ContainsKey(candidate));
            if (slot == 0)
            {
                throw new X86Exception(X86ErrorCodes.NetworkUnavailable, $"The private network has no free address: {AddressPlan.LastVmSlot - AddressPlan.FirstVmSlot + 1} VMs run already.");
            }

            var attachment = new VmNetworkAttachment(this, vmId, vmName, slot);
            _vms[slot] = attachment;
            _switch.Attach(slot, port);
            return attachment;
        }
    }

    /// <summary>The address and slots of a VM that runs, or <see langword="null"/>.</summary>
    public VmNetworkAttachment? Find(Guid vmId)
    {
        lock (Sync)
        {
            return _vms.Values.FirstOrDefault(vm => vm.VmId == vmId);
        }
    }

    /// <summary>Removes the rules of a deleted prefix, or of a deleted VM (<paramref name="prefix"/> null), and ends their flows.</summary>
    public async ValueTask ForgetAsync(Guid vmId, PrefixId? prefix, CancellationToken cancellationToken)
    {
        lock (Sync)
        {
            if (prefix is { } named)
            {
                _gateway.CloseIdentity(new FlowIdentity(vmId, named));
            }
        }

        await Rules.RemoveForAsync(vmId, prefix, cancellationToken).ConfigureAwait(false);
    }

    void IGatewayHost.Deliver(int vmSlot, byte[] frame) => _switch.Deliver(vmSlot, frame);

    void IGatewayHost.Drop(string reason) => _switch.Drop(reason);

    SourceIdentity? IGatewayHost.Resolve(int vmSlot, Ipv4Address source)
    {
        if (!AddressPlan.TryResolve(source, out int slot, out int? prefixSlot) || slot != vmSlot || !_vms.TryGetValue(slot, out VmNetworkAttachment? vm))
        {
            return null;
        }

        if (prefixSlot is not int prefixIndex)
        {
            return new SourceIdentity(new FlowIdentity(vm.VmId, null), vm.VmName);
        }

        return vm.PrefixAt(prefixIndex) is { } prefix ? new SourceIdentity(new FlowIdentity(vm.VmId, prefix), vm.VmName) : null;
    }

    void IGatewayHost.Journal(NetworkJournalEntry entry) => _journalQueue.Writer.TryWrite(entry);

    void IGatewayHost.ReportError(Exception exception) => _hub.ReportError(exception);

    internal void Receive(int vmSlot, ReadOnlySpan<byte> frame)
    {
        lock (Sync)
        {
            _switch.Receive(vmSlot, frame);
        }
    }

    internal void Detach(VmNetworkAttachment attachment)
    {
        lock (Sync)
        {
            if (_vms.TryGetValue(attachment.VmSlot, out VmNetworkAttachment? current) && current == attachment)
            {
                _gateway.CloseVm(attachment.VmSlot);
                _switch.Detach(attachment.VmSlot);
                _vms.Remove(attachment.VmSlot);
            }
        }
    }

    internal void ClosePrefixFlows(Guid vmId, PrefixId prefix)
    {
        lock (Sync)
        {
            _gateway.CloseIdentity(new FlowIdentity(vmId, prefix));
        }
    }

    private void Tick()
    {
        try
        {
            lock (Sync)
            {
                _gateway.Tick();
            }
        }
        catch (Exception exception)
        {
            _hub.ReportError(exception);
        }
    }

    private async Task WriteJournalAsync()
    {
        await foreach (NetworkJournalEntry entry in _journalQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await Journal.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _hub.ReportError(exception);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync().ConfigureAwait(false);
        foreach (VmNetworkAttachment vm in Attached)
        {
            Detach(vm);
        }

        _journalQueue.Writer.TryComplete();
        await _journalWriter.ConfigureAwait(false);
    }
}

/// <summary>
/// A running VM plugged into its user's network: its slot and address, the slots of its prefixes
/// (given when a prefix gets its network namespace in the guest, kept for the run). Frames of the
/// VM's card go in through <see cref="Receive"/>; disposing unplugs it and ends its flows.
/// </summary>
public sealed class VmNetworkAttachment : IAsyncDisposable
{
    private readonly UserNetwork _network;
    private readonly Dictionary<int, PrefixId> _prefixes = [];

    internal VmNetworkAttachment(UserNetwork network, Guid vmId, string vmName, int vmSlot)
    {
        _network = network;
        VmId = vmId;
        VmName = vmName;
        VmSlot = vmSlot;
    }

    /// <summary>The VM.</summary>
    public Guid VmId { get; }

    /// <summary>The VM's name (for the permission window).</summary>
    public string VmName { get; }

    /// <summary>The slot of the VM in the plan.</summary>
    public int VmSlot { get; }

    /// <summary>The VM's address.</summary>
    public Ipv4Address Address => AddressPlan.VmAddress(VmSlot);

    /// <summary>The user's network.</summary>
    public UserNetwork Network => _network;

    /// <summary>A frame the VM's card sent.</summary>
    public void Receive(ReadOnlySpan<byte> frame) => _network.Receive(VmSlot, frame);

    /// <summary>The slot of a prefix, given on first use (the lowest free one).</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.NetworkUnavailable"/> when every prefix slot of the VM is taken.</exception>
    public int SlotOf(PrefixId prefix)
    {
        lock (_network.Sync)
        {
            foreach ((int slot, PrefixId known) in _prefixes)
            {
                if (known == prefix)
                {
                    return slot;
                }
            }

            int free = Enumerable.Range(0, AddressPlan.PrefixSlots).FirstOrDefault(candidate => !_prefixes.ContainsKey(candidate), -1);
            if (free < 0)
            {
                throw new X86Exception(X86ErrorCodes.NetworkUnavailable, $"The VM has {AddressPlan.PrefixSlots} prefixes on the network already.");
            }

            _prefixes[free] = prefix;
            return free;
        }
    }

    /// <summary>The address of a prefix (its slot given on first use).</summary>
    public Ipv4Address AddressOf(PrefixId prefix) => AddressPlan.PrefixAddress(VmSlot, SlotOf(prefix));

    /// <summary>Frees the slot of a deleted prefix and ends its flows.</summary>
    public void Release(PrefixId prefix)
    {
        _network.ClosePrefixFlows(VmId, prefix);
        lock (_network.Sync)
        {
            foreach (int slot in _prefixes.Where(pair => pair.Value == prefix).Select(pair => pair.Key).ToList())
            {
                _prefixes.Remove(slot);
            }
        }
    }

    internal PrefixId? PrefixAt(int slot) => _prefixes.TryGetValue(slot, out PrefixId prefix) ? prefix : null;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _network.Detach(this);
        return ValueTask.CompletedTask;
    }
}
