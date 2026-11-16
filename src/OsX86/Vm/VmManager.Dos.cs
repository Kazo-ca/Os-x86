using OsX86.Dos;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Programs;

namespace OsX86.Vm;

/// <content>
/// Work item 11: DOS programs in a FreeDOS VM shown in the terminal (decisions 111, 212 A, 214 A).
/// </content>
public sealed partial class VmManager
{
    /// <summary>The name of the VM Os-x86 makes for DOS programs when the user has none free.</summary>
    public const string DosVmName = "dos";

    /// <summary>
    /// The VM a DOS program runs in when the caller names none: the oldest of the user's DOS VMs that
    /// is stopped (a DOS VM runs one program per start: a running one is busy). When every DOS VM runs,
    /// or the user has none, Os-x86 makes one from the built-in profile <c>dos</c> (165 A: no question
    /// below the memory budget, which the start checks).
    /// </summary>
    /// <returns>The VM, and whether it was just made.</returns>
    public async ValueTask<(VmRecord Vm, bool Created)> ChooseDosVmAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<VmRecord> vms = await Registry.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        VmRecord? stopped = vms
            .Where(vm => vm.Profile.Kind == VmKind.Dos && StateOf(userId, vm.Id) == VmState.Stopped)
            .OrderBy(vm => vm.Created)
            .FirstOrDefault();
        if (stopped is not null)
        {
            return (stopped, false);
        }

        string name = DosVmName;
        for (int index = 2; vms.Any(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)); index++)
        {
            name = $"{DosVmName}-{index}";
        }

        return (await Registry.CreateAsync(userId, name, BuiltInProfiles.Dos, cancellationToken).ConfigureAwait(false), true);
    }

    /// <summary>
    /// Runs a DOS program (<c>application/x-dosexec</c>, 162 A) in the VM named or chosen: the program
    /// and its batch go on a program disk (<see cref="DosProgramDisk"/>), the VM starts with it and
    /// with the keyboard; the session shows the screen and takes the keys.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ProgramNotSupported"/> (not DOS, too big, arguments DOS cannot carry, not a DOS VM), <see cref="X86ErrorCodes.VmBusy"/>, <see cref="X86ErrorCodes.VmNotFound"/>, or a start failure.</exception>
    public async ValueTask<DosProgramSession> RunDosProgramAsync(Guid userId, DosRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentNullException.ThrowIfNull(options.Arguments);
        if (options.Content.Length > DosProgramDisk.MaxContentBytes)
        {
            throw new X86Exception(X86ErrorCodes.ProgramNotSupported, $"The program is more than the {DosProgramDisk.MaxContentBytes / (1024 * 1024)} MiB a DOS program disk holds.");
        }

        ProgramDetection detection = ProgramDetector.Detect(options.Content.Span[..Math.Min(options.Content.Length, ProgramDetector.FullLength)], ProgramDetector.ExtensionOf(options.FileName));
        if (detection.Kind != ProgramKind.Dos || WhyNotSupported(detection) is not null)
        {
            throw new X86Exception(X86ErrorCodes.ProgramNotSupported, WhyNotSupported(detection) ?? $"this is not a DOS program ({detection.Detail}).");
        }

        bool isMz = options.Content.Length >= 2 && options.Content.Span[0] == (byte)'M' && options.Content.Span[1] == (byte)'Z';
        string shortName = DosRunScript.ToShortName(options.FileName, isMz);
        string batch = DosRunScript.Batch(shortName, DosRunScript.FormatArguments(options.Arguments));
        byte[] disk = DosProgramDisk.Create(
            [(shortName, options.Content), (DosRunScript.BatchFileName, System.Text.Encoding.ASCII.GetBytes(batch))],
            _time.GetLocalNow().DateTime);
        Guid vmId = options.VmId ?? (await ChooseDosVmAsync(userId, cancellationToken).ConfigureAwait(false)).Vm.Id;
        VmInstance instance = await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false);
        return await instance.RunDosProgramAsync(disk, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A DOS program to run (<see cref="VmManager.RunDosProgramAsync"/>).</summary>
public sealed record DosRunOptions
{
    /// <summary>The file name of the program (<c>edit.com</c>); a path is reduced to its last part and to 8.3.</summary>
    public required string FileName { get; init; }

    /// <summary>The bytes of the program.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The arguments after the program name (printable ASCII, at most a DOS command tail).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The VM, or <see langword="null"/> to let Os-x86 choose a stopped DOS VM (or make one).</summary>
    public Guid? VmId { get; init; }
}
