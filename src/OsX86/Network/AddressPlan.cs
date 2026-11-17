using OsX86.Prefixes;

namespace OsX86.Network;

/// <summary>
/// The addresses of a user's private network (decision 161 A: one address per VM and per prefix, no
/// NAT; trial E6: the source address is the label the gateway reads). One plan for every user: two
/// users never share a switch, so their addresses never meet.
/// <code>
/// 10.86.0.1            the gateway of Os-x86 (the way out, through the relay)
/// 10.86.0.S/24         the VM in slot S (2..254), on its eth0
/// 10.86.S.4P+1/30      the guest side of the link of prefix slot P (0..63) in VM S (root namespace)
/// 10.86.S.4P+2/30      the prefix's own address, in its network namespace (its identity)
/// </code>
/// Slots are given when a VM starts and when a prefix gets its namespace, and freed when the VM
/// stops: an address names a VM or a prefix for one run, the identity it maps to is what lasts.
/// </summary>
public static class AddressPlan
{
    /// <summary>The first VM slot (1 is the gateway).</summary>
    public const int FirstVmSlot = 2;

    /// <summary>The last VM slot (255 is the broadcast of the VM segment).</summary>
    public const int LastVmSlot = 254;

    /// <summary>The number of prefix slots in one VM (a /30 each in its /24).</summary>
    public const int PrefixSlots = 64;

    /// <summary>The private network: 10.86.0.0/16.</summary>
    public static Ipv4Address Network { get; } = Ipv4Address.Of(10, 86, 0, 0);

    /// <summary>The length of the private network's prefix.</summary>
    public const int NetworkPrefixLength = 16;

    /// <summary>The address of the gateway, 10.86.0.1.</summary>
    public static Ipv4Address Gateway { get; } = Ipv4Address.Of(10, 86, 0, 1);

    /// <summary>The Ethernet address of the gateway (locally administered).</summary>
    public static MacAddress GatewayMac { get; } = new(0x52_54_00_86_00_01UL);

    /// <summary>The address of the VM in slot <paramref name="vmSlot"/>, on the VM segment 10.86.0.0/24.</summary>
    public static Ipv4Address VmAddress(int vmSlot)
    {
        CheckVmSlot(vmSlot);
        return Ipv4Address.Of(10, 86, 0, (byte)vmSlot);
    }

    /// <summary>The address of prefix slot <paramref name="prefixSlot"/> of VM <paramref name="vmSlot"/>: its identity.</summary>
    public static Ipv4Address PrefixAddress(int vmSlot, int prefixSlot)
    {
        CheckVmSlot(vmSlot);
        CheckPrefixSlot(prefixSlot);
        return Ipv4Address.Of(10, 86, (byte)vmSlot, (byte)((4 * prefixSlot) + 2));
    }

    /// <summary>The guest side of the prefix's link, in the VM's root namespace (its default route).</summary>
    public static Ipv4Address PrefixLinkAddress(int vmSlot, int prefixSlot)
    {
        CheckVmSlot(vmSlot);
        CheckPrefixSlot(prefixSlot);
        return Ipv4Address.Of(10, 86, (byte)vmSlot, (byte)((4 * prefixSlot) + 1));
    }

    /// <summary>Whether an address is on the private network (never a destination the relay sees).</summary>
    public static bool IsPrivate(Ipv4Address address) => address.IsIn(Network, NetworkPrefixLength);

    /// <summary>
    /// What a source address names: a VM (its slot, no prefix) or a prefix (both slots). False for an
    /// address of nobody: the gateway, a link address, a broadcast, anything outside the plan.
    /// </summary>
    public static bool TryResolve(Ipv4Address source, out int vmSlot, out int? prefixSlot)
    {
        vmSlot = 0;
        prefixSlot = null;
        if (!IsPrivate(source))
        {
            return false;
        }

        int third = source.Octet(2);
        int fourth = source.Octet(3);
        if (third == 0)
        {
            vmSlot = fourth;
            return fourth is >= FirstVmSlot and <= LastVmSlot;
        }

        if (third is < FirstVmSlot or > LastVmSlot || fourth % 4 != 2)
        {
            return false;
        }

        vmSlot = third;
        prefixSlot = fourth / 4;
        return true;
    }

    /// <summary>
    /// Whether the VM in slot <paramref name="vmSlot"/> may send from <paramref name="source"/>: its
    /// own address or one of its prefixes' subnet (the switch drops a frame that claims another VM's
    /// address, so a guest never speaks for another VM).
    /// </summary>
    public static bool MaySend(int vmSlot, Ipv4Address source) =>
        source == VmAddress(vmSlot) || source.IsIn(Ipv4Address.Of(10, 86, (byte)vmSlot, 0), 24);

    private static void CheckVmSlot(int vmSlot)
    {
        if (vmSlot is < FirstVmSlot or > LastVmSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(vmSlot), vmSlot, $"A VM slot is {FirstVmSlot} to {LastVmSlot}.");
        }
    }

    private static void CheckPrefixSlot(int prefixSlot)
    {
        if (prefixSlot is < 0 or >= PrefixSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixSlot), prefixSlot, $"A prefix slot is 0 to {PrefixSlots - 1}.");
        }
    }
}

/// <summary>
/// Who opens a flow (ADR 0255 of Kazo §1: one identity per flow, set by the Feature): a whole VM
/// (its root namespace, D22) or one prefix of a VM (D20: one network decision per prefix).
/// </summary>
/// <param name="VmId">The VM.</param>
/// <param name="Prefix">The prefix, or <see langword="null"/> for the VM itself.</param>
public readonly record struct FlowIdentity(Guid VmId, PrefixId? Prefix)
{
    /// <summary>
    /// The label the relay gets (ADR 0255: at most 128 characters of <c>[A-Za-z0-9._:-]</c>):
    /// <c>vm:&lt;guid&gt;</c> or <c>prefix:&lt;guid&gt;:&lt;name&gt;</c>, the GUID in 32 hexadecimal digits.
    /// </summary>
    public string RelayLabel => Prefix is { } prefix ? $"prefix:{VmId:N}:{prefix.Value}" : $"vm:{VmId:N}";

    /// <inheritdoc />
    public override string ToString() => RelayLabel;
}
