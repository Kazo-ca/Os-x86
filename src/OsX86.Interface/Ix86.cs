namespace OsX86.Interface;

/// <summary>
/// The contract of Os-x86, the x86 emulation feature of Kazo OS. An app embeds this package and asks
/// the OS for the service; it gets <see langword="null"/> when the feature is not installed
/// (decisions 10 B, 158 A). The contract is generic: it names virtual machines, programs and
/// consoles, never Wine (29 C). The service is bound to the calling user: every VM it names belongs
/// to that user (decision 190 A), and nothing of another user's VMs is reachable through it.
/// This surface is what the Terminal and BoxedWine need first (work item 6): the list and state of
/// the VMs, starting and stopping one, opening a console in it; then (work item 10) detecting the
/// type of a program and running a console program with its exit code. Managing prefixes and the
/// rest of the plan (§1.1) are added by the work items whose clients need them.
/// </summary>
public interface Ix86
{
    /// <summary>The version of the installed feature, as published (plan §13: formats are versioned).</summary>
    string Version { get; }

    /// <summary>Raised when one of the caller's VMs changes state (plan §1.2: started on demand, stopped with its last application).</summary>
    event EventHandler<VmStateChangedEventArgs>? VmStateChanged;

    /// <summary>Lists the virtual machines the calling user may use (plan §1.2, decision 190: one GUID per VM), with their current state.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<IReadOnlyList<VmSummary>> GetVmsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a VM by hand and keeps it running until <see cref="StopVmAsync"/> (decision 72.6: a VM
    /// started by hand stays up, unlike one started for an application). A no-op when it is already
    /// held this way; a VM started for applications becomes held.
    /// </summary>
    /// <param name="vmId">The VM.</param>
    /// <param name="cancellationToken">Cancels the wait, not the boot.</param>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, <see cref="X86ErrorCodes.ImageUnavailable"/>, <see cref="X86ErrorCodes.EngineUnavailable"/> or a start failure.</exception>
    /// <exception cref="VmMemoryBudgetExceededException">Starting it would exceed the memory budget (decision 165 A).</exception>
    ValueTask StartVmAsync(Guid vmId, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a VM: releases the manual hold and, when no application uses it any more, shuts it down.
    /// With <paramref name="force"/>, shuts it down at once, closing every application and console.
    /// </summary>
    /// <param name="vmId">The VM.</param>
    /// <param name="force">Whether to stop it even though applications use it.</param>
    /// <param name="cancellationToken">Cancels the wait, not the shutdown.</param>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, or <see cref="X86ErrorCodes.VmBusy"/> when applications use it and <paramref name="force"/> is false.</exception>
    ValueTask StopVmAsync(Guid vmId, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a console in a VM, starting it when needed (decision 166 A: a console is an application
    /// that keeps the VM up). Through the guest agent it is a pseudo-terminal with the user's shell
    /// (213 A); an image without agent gets the serial console when its profile declares one (212 A).
    /// Disposing the console closes it and releases the VM.
    /// </summary>
    /// <param name="vmId">The VM.</param>
    /// <param name="options">The prefix and the size of the console.</param>
    /// <param name="cancellationToken">Cancels the wait, not the boot.</param>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, <see cref="X86ErrorCodes.NoConsole"/>, <see cref="X86ErrorCodes.NoPrefix"/> or a start failure.</exception>
    /// <exception cref="VmMemoryBudgetExceededException">Starting the VM would exceed the memory budget (decision 165 A).</exception>
    ValueTask<IVmConsole> OpenConsoleAsync(Guid vmId, VmConsoleOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Tells what kind of program a file is (decisions 162 A and 240 D): its header first, its
    /// extension when the header says nothing, a contradiction refused; the answer carries a media
    /// type (255 A). Pure: no VM is started. BoxedWine and the Terminal call it before
    /// <see cref="RunProgramAsync"/>; 64 bytes suffice for the media type, 4 KiB give the details
    /// (width, console or windowed, .NET).
    /// </summary>
    /// <param name="header">The first bytes of the file.</param>
    /// <param name="fileName">The file name or its extension, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<ProgramDetection> DetectProgramAsync(ReadOnlyMemory<byte> header, string? fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a console program in a console (work item 10; decisions 110, 193 A, 213 A, 216 A): the
    /// program is detected, a compatible VM and the prefix are chosen when not given, the VM starts
    /// when needed, the file is copied into the prefix, and the program runs on a pseudo-terminal of
    /// the guest. The console carries its output, the keystrokes and Ctrl+C (216 A), and
    /// <see cref="IVmConsole.Exited"/> gives the program's real exit code (110). The console is an
    /// application of the VM (166 A); disposing it ends the program.
    /// </summary>
    /// <param name="options">The program, its arguments, and optionally the VM and the prefix.</param>
    /// <param name="cancellationToken">Cancels the wait, not the boot.</param>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ProgramNotSupported"/> (a DOS, Linux, windowed or 64-bit program, not yet), <see cref="X86ErrorCodes.PrefixNotFound"/>, <see cref="X86ErrorCodes.VmNotFound"/> or a start failure.</exception>
    /// <exception cref="VmMemoryBudgetExceededException">Starting the VM would exceed the memory budget (decision 165 A).</exception>
    ValueTask<IVmConsole> RunProgramAsync(ProgramRunOptions options, CancellationToken cancellationToken);
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
/// <param name="Image">The identifier of the image it boots (decision 187).</param>
/// <param name="ImageVersion">The version of that image the VM is pinned to (plan §13).</param>
/// <param name="MemoryMiB">The guest memory it is given when it starts, counted against the budget of 165 A.</param>
public sealed record VmSummary(Guid Id, string Name, VmKind Kind, VmState State, string Image, string ImageVersion, int MemoryMiB);

/// <summary>Arguments of <see cref="Ix86.VmStateChanged"/>.</summary>
public sealed class VmStateChangedEventArgs(Guid vmId, VmState previous, VmState current) : EventArgs
{
    /// <summary>The VM that changed.</summary>
    public Guid VmId { get; } = vmId;

    /// <summary>Its state before the change.</summary>
    public VmState Previous { get; } = previous;

    /// <summary>Its state after the change.</summary>
    public VmState Current { get; } = current;
}
