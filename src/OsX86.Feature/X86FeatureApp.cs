using Kazo.Os.App.Abstractions;

namespace OsX86.Feature;

/// <summary>
/// The manifest of Os-x86 (<c>ca.kazo.x86</c>, decision 222 A, replacing 172 A). The build generates
/// <c>manifest.json</c> from this class; it is never instantiated at run time. Packaged as the
/// terminal command <c>x86</c> until Kazo OS has the Feature app kind (ADR 0243); the capabilities
/// stay empty until the work items that need them (storage, network) land.
/// </summary>
[AppManifest]
public sealed class X86FeatureApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "ca.kazo.x86";

    /// <inheritdoc />
    public override string Name => "Os-x86";

    /// <summary>The version of the feature; a tag v&lt;version&gt; publishes it (store workflows).</summary>
    public override string Version => Vm.X86Service.FeatureVersion;

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(X86Command);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazo";

    /// <inheritdoc />
    public override string Description => "Runs x86 virtual machines (Linux, DOS, Windows programs through Wine) inside Kazo OS.";

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("x86", "Manages x86 virtual machines and runs their programs", []);
}
