namespace OsX86.Feature;

/// <summary>
/// The permissions Os-x86 declares (ADR 0246 of Kazo, decision 176 A), under its capability prefix.
/// The operations of <see cref="Interface.Ix86"/> require them of the app that calls.
/// </summary>
public static class X86Permissions
{
    /// <summary>The capability prefix of the feature (ADR 0246 §2); also the prefix of its roles (ADR 0283).</summary>
    public const string Prefix = "x86";

    /// <summary>Run programs in a VM (normal, D32).</summary>
    public const string ProgramsLaunch = "x86.programs.launch";

    /// <summary>Use one's own VMs: list, start, stop, open a console (normal).</summary>
    public const string VmUse = "x86.vm.use";

    /// <summary>Delete or export one's own VMs, disks and restore points (dangerous).</summary>
    public const string VmDestroy = "x86.vm.destroy";

    /// <summary>Everything on one's own VMs; covers <see cref="VmUse"/> and <see cref="VmDestroy"/> (dangerous).</summary>
    public const string Vm = "x86.vm";
}
