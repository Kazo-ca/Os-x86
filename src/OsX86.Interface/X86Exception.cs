namespace OsX86.Interface;

/// <summary>The codes of <see cref="X86Exception"/>: stable identifiers a client can test, apart from the message.</summary>
public static class X86ErrorCodes
{
    /// <summary>No VM of the caller has this identifier or name.</summary>
    public const string VmNotFound = "vm-not-found";

    /// <summary>A VM name is empty, too long, has control characters, or is already used by the caller.</summary>
    public const string InvalidName = "invalid-name";

    /// <summary>A profile is not valid: unknown format, missing field, memory out of range.</summary>
    public const string InvalidProfile = "invalid-profile";

    /// <summary>A stored document has a format this version of Os-x86 does not read (decision 169: refused, nothing written).</summary>
    public const string UnsupportedFormat = "unsupported-format";

    /// <summary>The operation needs the VM stopped, or applications still use it (a stop without force).</summary>
    public const string VmBusy = "vm-busy";

    /// <summary>Starting the VM would exceed the memory budget (decision 165 A); see <see cref="VmMemoryBudgetExceededException"/>.</summary>
    public const string MemoryBudgetExceeded = "memory-budget-exceeded";

    /// <summary>The image of the VM is not in the local store, or this version of Os-x86 cannot boot it.</summary>
    public const string ImageUnavailable = "image-unavailable";

    /// <summary>This host cannot create an engine (the command runs where no emulator can; work item 8 gives the Feature one).</summary>
    public const string EngineUnavailable = "engine-unavailable";

    /// <summary>The engine failed to boot the VM, or stopped by itself.</summary>
    public const string StartFailed = "start-failed";

    /// <summary>The image has no agent and its profile declares no serial port: no console can be opened (decision 212 A).</summary>
    public const string NoConsole = "no-console";

    /// <summary>No prefix was given and the profile of the VM has no default one.</summary>
    public const string NoPrefix = "no-prefix";

    /// <summary>The guest agent refused or failed the operation; the message carries its code.</summary>
    public const string AgentError = "agent-error";

    /// <summary>The guest agent stopped answering (the watchdog of the VM); applications that need it fail.</summary>
    public const string AgentLost = "agent-lost";

    /// <summary>The console is closed: its program ended or the VM stopped.</summary>
    public const string ConsoleClosed = "console-closed";

    /// <summary>The user identifier of the caller is not a stable identifier (decision 190 A: a GUID).</summary>
    public const string InvalidUser = "invalid-user";

    /// <summary>The image store failed an operation on the files of a VM (its write layers, work item 6b).</summary>
    public const string StorageFailed = "storage-failed";

    /// <summary>The VM has no prefix of that name: make it with <c>x86 prefix create</c> (work item 10).</summary>
    public const string PrefixNotFound = "prefix-not-found";

    /// <summary>The VM already has a prefix of that name.</summary>
    public const string PrefixExists = "prefix-exists";

    /// <summary>
    /// The program is not one Os-x86 can run here yet: a DOS program (work item 11), a Linux program,
    /// a windowed program or an installer (they need a display), a 64-bit program, or a file that is
    /// not a program. The message says which, and what will come.
    /// </summary>
    public const string ProgramNotSupported = "program-not-supported";
}

/// <summary>An error of Os-x86 reported to a client, with a stable code.</summary>
public class X86Exception : Exception
{
    /// <summary>Creates the error.</summary>
    /// <param name="code">One of <see cref="X86ErrorCodes"/>.</param>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause, when there is one.</param>
    public X86Exception(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>One of <see cref="X86ErrorCodes"/>.</summary>
    public string Code { get; }
}

/// <summary>
/// Starting a VM was refused because the memory of the running VMs plus its own would exceed the
/// budget (decision 165 A: half of the memory the browser announces by default, set by the
/// administrator). The client offers the choice: stop one of <see cref="Running"/>, reuse one of
/// <see cref="Compatible"/> (same kind, same image), or give up. Nothing was started.
/// </summary>
public sealed class VmMemoryBudgetExceededException : X86Exception
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="requestedMiB">The memory of the VM that was refused.</param>
    /// <param name="runningMiB">The memory of every running VM, the caller's and the others'.</param>
    /// <param name="limitMiB">The budget.</param>
    /// <param name="running">The caller's running VMs; other users' VMs are counted in <paramref name="runningMiB"/> but never named.</param>
    /// <param name="compatible">Those of <paramref name="running"/> that have the same kind and image as the refused VM.</param>
    public VmMemoryBudgetExceededException(int requestedMiB, int runningMiB, int limitMiB, IReadOnlyList<VmSummary> running, IReadOnlyList<VmSummary> compatible)
        : base(X86ErrorCodes.MemoryBudgetExceeded, $"Starting this VM ({requestedMiB} MiB) with {runningMiB} MiB of VMs already running would exceed the budget of {limitMiB} MiB.")
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(compatible);
        RequestedMiB = requestedMiB;
        RunningMiB = runningMiB;
        LimitMiB = limitMiB;
        Running = running;
        Compatible = compatible;
    }

    /// <summary>The memory of the VM that was refused.</summary>
    public int RequestedMiB { get; }

    /// <summary>The memory of every running VM.</summary>
    public int RunningMiB { get; }

    /// <summary>The budget.</summary>
    public int LimitMiB { get; }

    /// <summary>The caller's running VMs (other users' VMs are never named).</summary>
    public IReadOnlyList<VmSummary> Running { get; }

    /// <summary>The caller's running VMs the refused one could be replaced by (same kind, same image).</summary>
    public IReadOnlyList<VmSummary> Compatible { get; }
}
