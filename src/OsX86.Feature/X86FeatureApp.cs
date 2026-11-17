using Kazo.Os.App.Abstractions;
using OsX86.Interface;

namespace OsX86.Feature;

/// <summary>
/// The manifest of Os-x86 (<c>ca.kazo.x86</c>, decision 222 A): a Feature of Kazo OS (ADR 0260,
/// work item 8). The build generates <c>manifest.json</c> and one <c>entrypoints/&lt;suffix&gt;.json</c>
/// per child from this class; it is never instantiated at run time. The feature has no entry point
/// of its own: its child is the <c>x86</c> command (<see cref="CommandSuffix"/>); it provides
/// <see cref="Ix86"/> to other apps (ADR 0262), declares the permissions of decision 176 A under the
/// prefix <c>x86</c> (ADR 0246) and the role <see cref="VmManagerRole"/> (ADR 0247, 281 A).
/// </summary>
[AppManifest]
public sealed class X86FeatureApp : FeatureDefinition
{
    /// <summary>The suffix of the command child, <c>ca.kazo.x86.cli</c>.</summary>
    public const string CommandSuffix = "cli";

    /// <summary>The role of the VM managers (281 A); work item 7 gives it the admin entry points.</summary>
    public const string VmManagerRole = "x86:vm-manager";

    /// <inheritdoc />
    public override string Id => "ca.kazo.x86";

    /// <inheritdoc />
    public override string Name => "Os-x86";

    /// <summary>The version of the feature; a tag v&lt;version&gt; publishes it (store workflows).</summary>
    public override string Version => Vm.X86Service.FeatureVersion;

    /// <inheritdoc />
    public override string PublisherId => "pub.kazo";

    /// <inheritdoc />
    public override string Description => "Runs x86 virtual machines (Linux, DOS, Windows programs through Wine) inside Kazo OS.";

    /// <summary>
    /// The set of the feature (ADR 0260 §3), shown once at installation. The VM registry and the
    /// imported profiles live in each user's private settings directory (work item 6, ADR 0044).
    /// </summary>
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite];

    /// <inheritdoc />
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
    [
        // ADR 0070 of Kazo: the VMs of the machine live on the page's main thread, so does the
        // command that drives them (work item 6b).
        new(CommandSuffix, "x86", AppKind.Terminal, typeof(X86Command))
        {
            Description = "Manages x86 virtual machines and runs their programs",
            Terminal = new("x86", "Manages x86 virtual machines and runs their programs", []),
            Capabilities = [AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite],
            Execution = AppExecutionHost.MainThread,
        },
    ];

    /// <summary>The contract other apps ask for with <c>KazoFeatures.GetServiceAsync&lt;Ix86&gt;()</c> (ADR 0262).</summary>
    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(Ix86), typeof(X86FeatureService))];

    /// <summary>The prefix of the permissions and the role (ADR 0246 §2, ADR 0283).</summary>
    public override string? CapabilityPrefix => X86Permissions.Prefix;

    /// <summary>The permissions of decision 176 A, which the operations of <see cref="Ix86"/> require of their callers.</summary>
    public override IReadOnlyList<ProvidedCapability> ProvidedCapabilities =>
    [
        new(X86Permissions.ProgramsLaunch, CapabilityRisk.Normal, "Run programs in x86 virtual machines"),
        new(X86Permissions.VmUse, CapabilityRisk.Normal, "Use your x86 virtual machines"),
        new(X86Permissions.VmDestroy, CapabilityRisk.Dangerous, "Delete or export your x86 virtual machines")
        {
            RiskStatement = "The app can delete your virtual machines, their disks and restore points, or copy them out.",
        },
        new(X86Permissions.Vm, CapabilityRisk.Dangerous, "Manage your x86 virtual machines")
        {
            RiskStatement = "The app can use, delete or export every virtual machine of yours.",
            Covers = [X86Permissions.VmUse, X86Permissions.VmDestroy],
        },
    ];

    /// <summary>The role of 281 A; it opens nothing until work item 7 adds the admin entry points.</summary>
    public override IReadOnlyList<FeatureRole> Roles =>
    [
        new(VmManagerRole, "VM manager")
        {
            Description = "Manages the x86 virtual machines of the machine: memory limit, VMs of other users.",
        },
    ];
}
