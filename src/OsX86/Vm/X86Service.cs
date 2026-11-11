using OsX86.Interface;

namespace OsX86.Vm;

/// <summary>
/// The implementation of <see cref="Ix86"/> that the feature registers in Kazo OS (plan §1.1,
/// x86-Lib). This first version only answers the version and an empty VM list; the VM registry
/// (work item 6), the engine (2) and the storage (3) fill it in.
/// </summary>
public sealed class X86Service : Ix86
{
    /// <summary>The version of the feature, kept in one place for the command and the contract.</summary>
    public const string FeatureVersion = "0.1.0";

    /// <inheritdoc />
    public string Version => FeatureVersion;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<VmSummary>> GetVmsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<VmSummary>>([]);
    }
}
