using System.Globalization;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>
/// What the command shares across its invocations in one host process: the VM manager of the
/// machine (the VMs keep running between two <c>x86</c> commands) and the files. Until Kazo OS
/// has the Feature app kind (work item 8), the command has no emulator of its own (a command gets
/// no <c>IJSRuntime</c>, ADR 0070 of Kazo): its engine factory refuses, and the hosts that have
/// one install theirs: the dev host through <see cref="VmHost.Configure"/> (work item 6b), the
/// guest tests through <see cref="Configure"/>, later the Feature.
/// </summary>
internal static class X86Runtime
{
    private static readonly Lock s_lock = new();
    private static X86CommandServices? s_services;

    /// <summary>The services of the command; created on first use with the defaults of a Kazo host.</summary>
    public static X86CommandServices Services
    {
        get
        {
            lock (s_lock)
            {
                return s_services ??= CreateDefault();
            }
        }
    }

    /// <summary>Replaces the services (tests, a host with an engine).</summary>
    public static void Configure(X86CommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (s_lock)
        {
            s_services = services;
        }
    }

    /// <summary>
    /// Reads the stable identifier of a Kazo user (decision 190 A): Kazo gives it as the 32 hexadecimal
    /// digits of a GUID (<c>LocalUserId.ToString()</c>); any GUID format is accepted, a login name is not.
    /// </summary>
    public static bool TryParseUserId(string? text, out Guid userId) =>
        Guid.TryParse(text, CultureInfo.InvariantCulture, out userId) && userId != Guid.Empty;

    private static X86CommandServices CreateDefault()
    {
        VmHostEnvironment? host = VmHost.Current; // the dev host installs one (work item 6b), the Feature later (work item 8)
        var manager = new VmManager(
            new KazoVmRegistryStore(KazoX86Files.Instance),
            host?.Engines ?? UnavailableEngineFactory.Instance,
            host?.Images ?? StaticBootImageSource.Empty,
            host?.Memory ?? FixedHostMemory.Unknown,
            network: new Network.NetworkHub(
                new KazoNetworkStore(KazoX86Files.Instance),
                host?.Relay ?? Network.UnavailableRelay.Instance,
                host?.PermissionPrompt ?? Network.UnavailablePermissionPrompt.Instance));
        return new X86CommandServices(manager, KazoX86Files.Instance);
    }
}

/// <summary>What the command works with.</summary>
/// <param name="Manager">The VMs of the machine.</param>
/// <param name="Files">Profile files.</param>
internal sealed record X86CommandServices(VmManager Manager, IX86Files Files);
