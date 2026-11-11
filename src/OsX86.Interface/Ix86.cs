namespace OsX86.Interface;

/// <summary>
/// The contract of Os-x86, the x86 emulation feature of Kazo OS. An app embeds this package and asks
/// the OS for the service; it gets <see langword="null"/> when the feature is not installed
/// (decisions 10 B, 158 A). The contract is generic: it names virtual machines, programs and
/// consoles, never Wine (29 C). This first surface only identifies the feature and lists the
/// caller's virtual machines; the launch, console and control operations of the plan (§1.1) are added
/// by the work items that implement them.
/// </summary>
public interface Ix86
{
    /// <summary>The version of the installed feature, as published (plan §13: formats are versioned).</summary>
    string Version { get; }

    /// <summary>Lists the virtual machines the calling user may use (plan §1.2, decision 190: one GUID per VM).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<IReadOnlyList<VmSummary>> GetVmsAsync(CancellationToken cancellationToken);
}

/// <summary>What a virtual machine runs (plan §1.1: one VM per kind and image in V1, decision 194 A).</summary>
public enum VmKind
{
    /// <summary>A Linux guest (console and, later, graphical programs).</summary>
    Linux = 0,

    /// <summary>A FreeDOS guest (decision 111).</summary>
    Dos = 1,

    /// <summary>The Linux guest that runs Windows programs through Wine, seen by clients as "Windows programs" (9 B).</summary>
    WindowsPrograms = 2,
}

/// <summary>The lifecycle state of a virtual machine (plan §1.2: started on demand, stopped with its last application, 166 A).</summary>
public enum VmState
{
    /// <summary>Not running; its disks are closed.</summary>
    Stopped = 0,

    /// <summary>Booting; the guest agent has not reported <c>ready</c> yet.</summary>
    Starting = 1,

    /// <summary>Running and reachable.</summary>
    Running = 2,

    /// <summary>Shutting down; its last application closed or an administrator stopped it.</summary>
    Stopping = 3,
}

/// <summary>One virtual machine as a client sees it.</summary>
/// <param name="Id">The stable identifier of the VM (decision 190: a GUID, never its name).</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Kind">What the VM runs.</param>
/// <param name="State">Its current lifecycle state.</param>
public sealed record VmSummary(Guid Id, string Name, VmKind Kind, VmState State);
