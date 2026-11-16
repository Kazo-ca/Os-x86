using OsX86.Agent;
using OsX86.Interface;
using OsX86.Prefixes;
using OsX86.Profiles;
using OsX86.Programs;

namespace OsX86.Vm;

/// <content>
/// Work item 10: the prefixes of a user's VMs (decisions 193 A, 200 B) and the programs run in a
/// console (162 A, 194 A: one VM per kind and image for all of the user's prefixes).
/// </content>
public sealed partial class VmManager
{
    /// <summary>The name of the VM Os-x86 makes for Windows programs when the user has none.</summary>
    public const string WindowsVmName = "windows";

    /// <summary>
    /// Checks a program and returns why it cannot run here yet, or <see langword="null"/> when it can:
    /// a 32-bit console program for i386 (with .NET or not), or a batch file.
    /// </summary>
    public static string? WhyNotSupported(ProgramDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        return detection switch
        {
            { Source: ProgramDetectionSource.Conflict } => $"the file is refused: {detection.Detail} (its header and its extension disagree, decision 162 A).",
            { Kind: ProgramKind.Unknown } => $"this is not a program Os-x86 knows ({detection.Detail}).",
            { Kind: ProgramKind.Dos } => "this is a DOS program: DOS programs will run in a FreeDOS VM in the terminal (work item 11, with ADR 0251 and 0252 of Kazo).",
            { Kind: ProgramKind.Linux } => "this is a Linux program: `x86 run` does not run Linux programs yet; open `x86 shell` in a Linux VM and run it there.",
            { Kind: ProgramKind.WindowsInstaller } => "this is a Windows Installer package: installers need a display, which comes with BoxedWine (work item 13).",
            { Kind: ProgramKind.Windows, IsLibrary: true } => "this is a Windows library (DLL), not a program.",
            { Kind: ProgramKind.Windows, Bits: 64 } => "this is a 64-bit Windows program: the emulated processor (v86) is 32-bit, it cannot run.",
            { Kind: ProgramKind.Windows, Bits: 16 } => "this is a 16-bit Windows program: it needs a display, which comes with BoxedWine (work item 13).",
            { Kind: ProgramKind.Windows, Subsystem: ProgramSubsystem.Windowed } => "this is a windowed Windows program: windowed programs need a display, which comes with BoxedWine (work item 13); `x86 run` runs console programs.",
            { Kind: ProgramKind.Windows, Subsystem: ProgramSubsystem.Other } => $"this Windows program is not for the console nor for a window ({detection.Detail}).",
            { Kind: ProgramKind.Windows } when !detection.Detail.Contains("i386", StringComparison.Ordinal) && detection.IsComplete => $"this Windows program is not for an x86 processor ({detection.Detail}).",
            _ => null,
        };
    }

    /// <summary>
    /// The VM a program runs in when the caller names none (194 A): the user's only VM of the
    /// program's kind; among several, the running one, else the oldest. When the user has none, Os-x86
    /// makes one from the built-in profile (<c>wine</c> for Windows programs).
    /// </summary>
    /// <returns>The VM, and whether it was just made.</returns>
    public async ValueTask<(VmRecord Vm, bool Created)> ChooseVmAsync(Guid userId, VmKind kind, CancellationToken cancellationToken)
    {
        IReadOnlyList<VmRecord> vms = await Registry.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        List<VmRecord> compatible = [.. vms.Where(vm => vm.Profile.Kind == kind)];
        if (compatible.Count > 0)
        {
            VmRecord chosen = compatible.FirstOrDefault(vm => StateOf(userId, vm.Id) is VmState.Running or VmState.Starting) ?? compatible.OrderBy(vm => vm.Created).First();
            return (chosen, false);
        }

        VmProfile profile = kind == VmKind.WindowsPrograms
            ? BuiltInProfiles.Wine
            : throw new X86Exception(X86ErrorCodes.VmNotFound, $"You have no virtual machine of the kind {VmKindJsonConverter.ToName(kind)}.");
        string name = WindowsVmName;
        for (int index = 2; vms.Any(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)); index++)
        {
            name = $"{WindowsVmName}-{index}";
        }

        return (await Registry.CreateAsync(userId, name, profile, cancellationToken).ConfigureAwait(false), true);
    }

    /// <summary>
    /// The prefix a program or a console uses in a VM (193 A): the one named, which must exist, or the
    /// VM's default prefix (recorded on its first use).
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.PrefixNotFound"/>, or <see cref="X86ErrorCodes.InvalidName"/> for a malformed name.</exception>
    public async ValueTask<PrefixId> ResolvePrefixAsync(Guid userId, Guid vmId, string? prefix, CancellationToken cancellationToken)
    {
        VmRecord vm = await Registry.FindAsync(userId, vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No virtual machine with the identifier {vmId}.");
        PrefixId id = vm.DefaultPrefix;
        if (prefix is not null && !PrefixId.TryParse(prefix, out id))
        {
            throw new X86Exception(X86ErrorCodes.InvalidName, $"'{prefix}' is not a prefix name (lowercase letters, digits and dashes, 1 to {LaunchId.MaxPrefixLength} characters).");
        }

        if (!vm.KnowsPrefix(id))
        {
            throw new X86Exception(X86ErrorCodes.PrefixNotFound, $"The virtual machine {vm.Name} has no prefix named '{id}': make it with `x86 prefix create {id} --vm {vm.Name}`.");
        }

        if (id == vm.DefaultPrefix)
        {
            await Registry.AddPrefixAsync(userId, vmId, id, ifMissing: true, cancellationToken).ConfigureAwait(false);
        }

        return id;
    }

    /// <summary>
    /// Runs a console program (work item 10): detected (162 A), refused when it cannot run yet, in the
    /// VM named or chosen (194 A), in the prefix named or the default one (193 A).
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ProgramNotSupported"/>, <see cref="X86ErrorCodes.PrefixNotFound"/>, <see cref="X86ErrorCodes.VmNotFound"/>, or a start failure.</exception>
    public async ValueTask<IVmConsole> RunProgramAsync(Guid userId, ProgramRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentNullException.ThrowIfNull(options.Arguments);
        if (options.Content.Length > ProgramRunOptions.MaxContentBytes)
        {
            throw new X86Exception(X86ErrorCodes.ProgramNotSupported, $"The program is more than the {ProgramRunOptions.MaxContentBytes / (1024 * 1024)} MiB `x86 run` copies into a prefix.");
        }

        ProgramDetection detection = ProgramDetector.Detect(options.Content.Span[..Math.Min(options.Content.Length, ProgramDetector.FullLength)], ProgramDetector.ExtensionOf(options.FileName));
        if (WhyNotSupported(detection) is { } why)
        {
            throw new X86Exception(X86ErrorCodes.ProgramNotSupported, why);
        }

        Guid vmId = options.VmId ?? (await ChooseVmAsync(userId, VmKind.WindowsPrograms, cancellationToken).ConfigureAwait(false)).Vm.Id;
        PrefixId prefix = await ResolvePrefixAsync(userId, vmId, options.Prefix, cancellationToken).ConfigureAwait(false);
        var program = new PreparedProgram
        {
            Prefix = prefix,
            FileName = PreparedProgram.SafeFileName(options.FileName),
            Content = options.Content,
            IsBatch = detection.Kind == ProgramKind.Batch,
            Arguments = options.Arguments,
            Columns = options.Columns,
            Rows = options.Rows,
        };
        VmInstance instance = await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false);
        return await instance.RunProgramAsync(program, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a prefix in a VM (<c>x86 prefix create</c>): recorded at once; its guest user and files
    /// are made now when the VM runs, otherwise at its first use (no VM is started for it).
    /// </summary>
    /// <returns>Whether the guest was reached.</returns>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.PrefixExists"/>, <see cref="X86ErrorCodes.VmNotFound"/>.</exception>
    public async ValueTask<bool> CreatePrefixAsync(Guid userId, Guid vmId, PrefixId prefix, CancellationToken cancellationToken)
    {
        await Registry.AddPrefixAsync(userId, vmId, prefix, ifMissing: false, cancellationToken).ConfigureAwait(false);
        VmInstance instance = await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false);
        return await instance.MaterializePrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a prefix (<c>x86 prefix delete</c>): when the VM runs, its programs are killed and its
    /// guest user and files removed now (refused while its programs run, unless <paramref name="force"/>);
    /// a stopped VM removes them at its next start, before anything runs.
    /// </summary>
    /// <returns>Whether the guest was reached now.</returns>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.PrefixNotFound"/>, <see cref="X86ErrorCodes.VmBusy"/>, <see cref="X86ErrorCodes.VmNotFound"/>.</exception>
    public async ValueTask<bool> DeletePrefixAsync(Guid userId, Guid vmId, PrefixId prefix, bool force, CancellationToken cancellationToken)
    {
        VmRecord vm = await Registry.FindAsync(userId, vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No virtual machine with the identifier {vmId}.");
        if (!vm.KnowsPrefix(prefix))
        {
            throw new X86Exception(X86ErrorCodes.PrefixNotFound, $"The virtual machine {vm.Name} has no prefix named '{prefix}'.");
        }

        VmInstance instance = await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false);
        int running = instance.Children.Count(child => child.Kind != VmChildKind.Session && child.Launch.Prefix == prefix.Value);
        if (running > 0 && !force)
        {
            throw new X86Exception(X86ErrorCodes.VmBusy, $"{running} program(s) or console(s) run in the prefix '{prefix}'; end them, or delete it with --force.");
        }

        bool deleted = await instance.DeleteGuestPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        await Registry.RemovePrefixAsync(userId, vmId, prefix, deleted, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>The prefixes of a VM: the recorded ones and the default one, with what the guest says when the VM runs.</summary>
    public async ValueTask<IReadOnlyList<PrefixState>> ListPrefixesAsync(Guid userId, Guid vmId, CancellationToken cancellationToken)
    {
        VmRecord vm = await Registry.FindAsync(userId, vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No virtual machine with the identifier {vmId}.");
        VmInstance instance = await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GuestPrefix>? guest = await instance.ListGuestPrefixesAsync(cancellationToken).ConfigureAwait(false);
        List<Prefix> prefixes = [.. vm.EffectivePrefixes];
        if (prefixes.All(p => p.Id != vm.DefaultPrefix))
        {
            prefixes.Insert(0, new Prefix(vm.DefaultPrefix, vm.Created));
        }

        return [.. prefixes.Select(p => new PrefixState(
            p,
            p.Id == vm.DefaultPrefix,
            guest is null ? null : guest.Any(g => g.Prefix == p.Id.Value),
            instance.HasSession(p.Id),
            instance.Children.Count(child => child.Kind != VmChildKind.Session && child.Launch.Prefix == p.Id.Value)))];
    }
}

/// <summary>A prefix of a VM as <c>x86 prefix list</c> shows it.</summary>
/// <param name="Prefix">The prefix.</param>
/// <param name="IsDefault">Whether it is the VM's default prefix (193 A).</param>
/// <param name="InGuest">Whether the guest has its user: <see langword="null"/> when the VM is stopped.</param>
/// <param name="HasSession">Whether its Wine session runs.</param>
/// <param name="Programs">How many programs and consoles run in it.</param>
public sealed record PrefixState(Prefix Prefix, bool IsDefault, bool? InGuest, bool HasSession, int Programs);
