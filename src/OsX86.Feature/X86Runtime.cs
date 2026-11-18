using System.Globalization;
using OsX86.Network;
using OsX86.Storage;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>
/// What the Feature shares across the invocations of its command and the calls of its service in
/// one page: the VM manager of the machine (the VMs keep running between two <c>x86</c> commands)
/// and the files. The emulator and the storage need the <c>IJSRuntime</c> of the page, which only
/// the entry points of the Feature receive (ADR 0055 of Kazo): its service child
/// <c>ca.kazo.x86.host</c> installs them at login, its command when it runs first (decision 288 A),
/// through <see cref="X86Host"/>, unless a host installed its own before (the dev host, the guest
/// tests through <see cref="Configure"/>).
/// </summary>
internal static class X86Runtime
{
    private static readonly Lock s_lock = new();
    private static X86CommandServices? s_services;
    private static bool s_madeWithoutHost;

    /// <summary>
    /// The services of the Feature; made on first use with what the host installed. Made before any
    /// host was installed, they have no emulator and keep their state in memory: they are made again,
    /// with the emulator and the state in the OPFS, the first time they are asked for once a host is installed.
    /// </summary>
    public static X86CommandServices Services
    {
        get
        {
            // Read once: the host may be installed by another entry point at any moment (VmHost has its own lock).
            VmHostEnvironment? host = VmHost.Current;
            lock (s_lock)
            {
                if (s_services is null || (s_madeWithoutHost && host is not null))
                {
                    // Made again only from services without host: their manager had no emulator, so no VM ran.
                    s_services = CreateDefault(host);
                    s_madeWithoutHost = host is null;
                }

                return s_services;
            }
        }
    }

    /// <summary>Replaces the services (tests, a host with an engine); they are never made again.</summary>
    public static void Configure(X86CommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (s_lock)
        {
            s_services = services;
            s_madeWithoutHost = false;
        }
    }

    /// <summary>Forgets the services (tests): the next use makes them again.</summary>
    internal static void Reset()
    {
        lock (s_lock)
        {
            s_services = null;
            s_madeWithoutHost = false;
        }
    }

    /// <summary>
    /// Reads the stable identifier of a Kazo user (decision 190 A): Kazo gives it as the 32 hexadecimal
    /// digits of a GUID (<c>LocalUserId.ToString()</c>); any GUID format is accepted, a login name is not.
    /// </summary>
    public static bool TryParseUserId(string? text, out Guid userId) =>
        Guid.TryParse(text, CultureInfo.InvariantCulture, out userId) && userId != Guid.Empty;

    /// <summary>
    /// The services over what the host installed. The state of each user is in the OPFS of the
    /// Feature, next to the disks (decision 287 A); a host without OPFS keeps it in memory, as it
    /// cannot run a VM anyway.
    /// </summary>
    private static X86CommandServices CreateDefault(VmHostEnvironment? host)
    {
        UserStateStore? state = host?.Store is { } store ? new UserStateStore(store) : null;
        var manager = new VmManager(
            state as IVmRegistryStore ?? new InMemoryVmRegistryStore(),
            host?.Engines ?? UnavailableEngineFactory.Instance,
            host?.Images ?? StaticBootImageSource.Empty,
            host?.Memory ?? FixedHostMemory.Unknown,
            network: new NetworkHub(
                state as INetworkStore ?? new InMemoryNetworkStore(),
                host?.Relay ?? UnavailableRelay.Instance,
                host?.PermissionPrompt ?? UnavailablePermissionPrompt.Instance));
        return new X86CommandServices(manager, KazoX86Files.Instance)
        {
            StorageTree = host?.Store is { } images ? new ImageStoreTree(images) : null,
        };
    }
}

/// <summary>What the command works with.</summary>
/// <param name="Manager">The VMs of the machine.</param>
/// <param name="Files">Profile files.</param>
internal sealed record X86CommandServices(VmManager Manager, IX86Files Files)
{
    /// <summary>What the Feature does with the storage of the browser (ADR 0250, 289 A); one per page.</summary>
    public FeatureStorageGuard Storage { get; init; } = new();

    /// <summary>The <c>osx86/</c> tree in the OPFS, or <see langword="null"/> on a host without storage.</summary>
    public IFeatureStorageTree? StorageTree { get; init; }
}
