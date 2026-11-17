namespace OsX86.Agent;

/// <content>
/// The operation of protocol 3 (work item 12): <c>net</c>, which runs the image's fixed script
/// <c>/usr/lib/osx86/net-admin</c> as root with numbers the agent checks (never a shell line):
/// the VM's own address on its card, the network namespace of a prefix with its address, the
/// removal of that namespace. The addresses follow from the slots (<c>Network.AddressPlan</c>).
/// </content>
public sealed partial class AgentClient
{
    /// <summary>The time the agent leaves <c>net-admin</c>.</summary>
    public static readonly TimeSpan NetAdminTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether the connected agent has the operation of protocol 3.</summary>
    public bool HasNetworkOperations => Status is { ProtocolVersion: >= 3 };

    /// <summary>
    /// Brings the VM's card up with the address of slot <paramref name="vmSlot"/> (10.86.0.<paramref name="vmSlot"/>/24,
    /// route through the gateway 10.86.0.1) and loads the filtering rules of the guest (174 A: nftables in defence).
    /// </summary>
    /// <returns>Whether something changed (false when it was already so).</returns>
    /// <exception cref="AgentException"><see cref="AgentErrorCodes.NoNetworkSupport"/>, <see cref="AgentErrorCodes.NetworkFailed"/>, <see cref="AgentErrorCodes.UnknownOperation"/>.</exception>
    public Task<bool> NetworkUpAsync(int vmSlot, CancellationToken cancellationToken)
    {
        CheckSlot(vmSlot, 2, 254, nameof(vmSlot));
        return NetAsync(new AgentRequest { Op = AgentProtocol.Operations.Net, Action = "vm-up", VmSlot = vmSlot }, cancellationToken);
    }

    /// <summary>
    /// Makes the network namespace of a prefix (<c>osx86-&lt;prefix&gt;</c>), its link to the VM's
    /// namespace and its own address (10.86.<paramref name="vmSlot"/>.4×<paramref name="prefixSlot"/>+2):
    /// what the agent launches in the prefix runs in it from then on.
    /// </summary>
    /// <returns>Whether something changed.</returns>
    public Task<bool> PrefixNetworkUpAsync(string prefix, int vmSlot, int prefixSlot, CancellationToken cancellationToken)
    {
        ValidatePrefix(prefix);
        CheckSlot(vmSlot, 2, 254, nameof(vmSlot));
        CheckSlot(prefixSlot, 0, 63, nameof(prefixSlot));
        return NetAsync(new AgentRequest { Op = AgentProtocol.Operations.Net, Action = "prefix-up", Prefix = prefix, VmSlot = vmSlot, PrefixSlot = prefixSlot }, cancellationToken);
    }

    /// <summary>Removes the network namespace of a prefix.</summary>
    /// <returns>Whether there was one.</returns>
    public Task<bool> PrefixNetworkDownAsync(string prefix, CancellationToken cancellationToken)
    {
        ValidatePrefix(prefix);
        return NetAsync(new AgentRequest { Op = AgentProtocol.Operations.Net, Action = "prefix-down", Prefix = prefix }, cancellationToken);
    }

    private async Task<bool> NetAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        AgentStatus? status = Status;
        if (status is not null && status.ProtocolVersion < 3)
        {
            throw new AgentException(AgentErrorCodes.UnknownOperation, $"The agent speaks version {status.ProtocolVersion} of the protocol; '{request.Op}' needs version 3.");
        }

        AgentMessage reply = await SendAsync(request, NetAdminTimeout + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
        return reply.Changed;
    }

    private static void CheckSlot(int value, int first, int last, string name)
    {
        if (value < first || value > last)
        {
            throw new ArgumentOutOfRangeException(name, value, $"A slot is {first} to {last}.");
        }
    }
}
