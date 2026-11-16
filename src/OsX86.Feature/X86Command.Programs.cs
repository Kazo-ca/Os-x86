using System.Globalization;
using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Prefixes;
using OsX86.Programs;
using OsX86.Vm;

namespace OsX86.Feature;

/// <content>Work item 10: <c>x86 prefix</c>, <c>x86 detect</c> and <c>x86 run</c> (DOS programs: work item 11, <c>X86Command.Dos.cs</c>).</content>
public sealed partial class X86Command
{
    /// <summary>What <c>x86 prefix export</c> says until prefixes can be exported (plan §11: with the disks and restore points).</summary>
    internal const string ExportNotYet = "x86: exporting a prefix is not available yet: it comes with the export of disks and restore points (plan section 11). The prefix stays in its virtual machine.";

    private static readonly string[] RunOptions = ["vm", "prefix", "cols", "rows"];

    private async ValueTask<int> PrefixAsync(TerminalExecutionContext context, Guid user, List<string> args, CancellationToken cancellationToken)
    {
        string verb = args.Count > 0 ? args[0] : string.Empty;
        (string[] flags, int positional) = verb switch
        {
            "list" => (NoOptions, 0),
            "create" or "export" => (NoOptions, 1),
            "delete" => (["force"], 1),
            _ => (NoOptions, -1),
        };
        if (positional < 0)
        {
            return await UsageErrorAsync(context, verb.Length == 0 ? "prefix needs a subcommand" : $"unknown command: prefix {verb}", cancellationToken).ConfigureAwait(false);
        }

        CommandArguments? parsed = CommandArguments.Parse(args.Skip(1), ["vm"], flags, out string? error);
        if (parsed is null || parsed.Positional.Count != positional)
        {
            return await UsageErrorAsync(context, error ?? $"prefix {verb} takes {positional} argument(s)", cancellationToken).ConfigureAwait(false);
        }

        PrefixId prefix = default;
        if (positional == 1 && !PrefixId.TryParse(parsed.Positional[0], out prefix))
        {
            throw new X86Exception(X86ErrorCodes.InvalidName, $"'{parsed.Positional[0]}' is not a prefix name (lowercase letters, digits and dashes, 1 to 16 characters).");
        }

        if (verb == "export")
        {
            await context.StandardError.WriteLineAsync(ExportNotYet.AsMemory(), cancellationToken).ConfigureAwait(false);
            return X86ExitCodes.Unavailable;
        }

        VmRecord? vm = await PrefixVmAsync(user, parsed.Value("vm"), cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            return await UsageErrorAsync(context, "you have several virtual machines: name one with --vm", cancellationToken).ConfigureAwait(false);
        }

        TextWriter output = context.StandardOutput;
        switch (verb)
        {
            case "list":
                await WriteLinesAsync(output, FormatPrefixes(vm, await Manager.ListPrefixesAsync(user, vm.Id, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            case "create":
            {
                bool now = await Manager.CreatePrefixAsync(user, vm.Id, prefix, cancellationToken).ConfigureAwait(false);
                string when = now ? "its guest user and files are ready" : "its guest user and files are made when it is first used";
                await output.WriteLineAsync($"Created the prefix {prefix} in {vm.Name}; {when}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            default:
            {
                bool now = await Manager.DeletePrefixAsync(user, vm.Id, prefix, parsed.Has("force"), cancellationToken).ConfigureAwait(false);
                string when = now ? "its programs, guest user and files are gone" : "its guest user and files are removed when the virtual machine next starts";
                await output.WriteLineAsync($"Deleted the prefix {prefix} of {vm.Name}; {when}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }
        }
    }

    /// <summary>
    /// The VM of a prefix command: the one named; else the user's only VM; else the only VM of
    /// Windows programs; <see langword="null"/> when that leaves several.
    /// </summary>
    private async ValueTask<VmRecord?> PrefixVmAsync(Guid user, string? named, CancellationToken cancellationToken)
    {
        if (named is not null)
        {
            return await Manager.ResolveAsync(user, named, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<VmRecord> vms = await Manager.Registry.ListAsync(user, cancellationToken).ConfigureAwait(false);
        if (vms.Count == 0)
        {
            throw new X86Exception(X86ErrorCodes.VmNotFound, "You have no virtual machine: `x86 run` makes one for Windows programs, or create one with `x86 vm create <name>`.");
        }

        List<VmRecord> windows = [.. vms.Where(v => v.Profile.Kind == VmKind.WindowsPrograms)];
        return vms.Count == 1 ? vms[0] : windows.Count == 1 ? windows[0] : null;
    }

    internal static IEnumerable<string> FormatPrefixes(VmRecord vm, IReadOnlyList<PrefixState> prefixes)
    {
        yield return $"Prefixes of {vm.Name} ({vm.Id}):";
        yield return string.Format(CultureInfo.InvariantCulture, "{0,-18}{1,-9}{2,-7}{3,-9}{4,-10}{5}", "PREFIX", "DEFAULT", "GUEST", "SESSION", "PROGRAMS", "CREATED");
        foreach (PrefixState state in prefixes)
        {
            string guest = state.InGuest switch { true => "yes", false => "no", null => "-" };
            yield return string.Format(
                CultureInfo.InvariantCulture,
                "{0,-18}{1,-9}{2,-7}{3,-9}{4,-10}{5:yyyy-MM-dd HH:mm}",
                state.Prefix.Id,
                state.IsDefault ? "yes" : "no",
                guest,
                state.HasSession ? "running" : "-",
                state.Programs,
                state.Prefix.Created.ToUniversalTime());
        }

        yield return "GUEST: whether the guest has the prefix's user (- while the virtual machine is stopped).";
    }

    private async ValueTask<int> DetectAsync(TerminalExecutionContext context, List<string> args, CancellationToken cancellationToken)
    {
        CommandArguments? parsed = CommandArguments.Parse(args, NoOptions, NoOptions, out string? error);
        if (parsed is null || parsed.Positional.Count != 1)
        {
            return await UsageErrorAsync(context, error ?? "detect takes one file", cancellationToken).ConfigureAwait(false);
        }

        string path = parsed.Positional[0];
        byte[]? header = await _services.Files.ReadBytesAsync(path, ProgramDetector.FullLength, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            await context.StandardError.WriteLineAsync($"x86: no file {path}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return X86ExitCodes.NotFound;
        }

        ProgramDetection detection = ProgramDetector.Detect(header, ProgramDetector.ExtensionOf(path));
        await WriteLinesAsync(context.StandardOutput, FormatDetection(path, detection), cancellationToken).ConfigureAwait(false);
        return X86ExitCodes.Ok;
    }

    internal static IEnumerable<string> FormatDetection(string path, ProgramDetection detection)
    {
        string source = detection.Source switch
        {
            ProgramDetectionSource.Header => "by its header",
            ProgramDetectionSource.Extension => "by its extension",
            ProgramDetectionSource.Conflict => "refused: its header and its extension disagree",
            _ => "neither its header nor its extension says",
        };
        yield return $"{path}: {detection.MimeType}";
        yield return $"  {detection.Detail} ({source})";
        string? why = VmManager.WhyNotSupported(detection);
        yield return why is not null ? $"  x86 run: no, {why}"
            : detection.Kind == ProgramKind.Dos ? "  x86 run: runs it in a FreeDOS virtual machine, its screen in the terminal"
            : "  x86 run: runs it in a virtual machine of Windows programs";
    }

    private async ValueTask<int> RunAsync(TerminalExecutionContext context, Guid user, List<string> args, CancellationToken cancellationToken)
    {
        if (!TryParseRun(args, out Dictionary<string, string> options, out string? program, out List<string> arguments, out string? error))
        {
            return await UsageErrorAsync(context, error!, cancellationToken).ConfigureAwait(false);
        }

        if (!TryParseSize(options.GetValueOrDefault("cols"), 80, out int columns) || !TryParseSize(options.GetValueOrDefault("rows"), 25, out int rows))
        {
            return await UsageErrorAsync(context, "--cols and --rows are whole numbers from 1 to 1000", cancellationToken).ConfigureAwait(false);
        }

        byte[]? content = await _services.Files.ReadBytesAsync(program!, ProgramRunOptions.MaxContentBytes + 1, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            await context.StandardError.WriteLineAsync($"x86: no file {program}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return X86ExitCodes.NotFound;
        }

        ProgramDetection detection = ProgramDetector.Detect(content.AsSpan(0, Math.Min(content.Length, ProgramDetector.FullLength)), ProgramDetector.ExtensionOf(program));
        if (VmManager.WhyNotSupported(detection) is { } why)
        {
            throw new X86Exception(X86ErrorCodes.ProgramNotSupported, $"{program}: {why}");
        }

        if (detection.Kind == ProgramKind.Dos)
        {
            return await RunDosAsync(context, user, options, program!, content, arguments, cancellationToken).ConfigureAwait(false);
        }

        Guid vmId;
        if (options.TryGetValue("vm", out string? named))
        {
            vmId = (await Manager.ResolveAsync(user, named, cancellationToken).ConfigureAwait(false)).Id;
        }
        else
        {
            (VmRecord chosen, bool created) = await Manager.ChooseVmAsync(user, VmKind.WindowsPrograms, cancellationToken).ConfigureAwait(false);
            vmId = chosen.Id;
            if (created)
            {
                await context.StandardError.WriteLineAsync($"x86: created the virtual machine {chosen.Name} ({chosen.Id}) for Windows programs, from the profile {chosen.Profile.Name}.".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        var run = new ProgramRunOptions
        {
            FileName = program!,
            Content = content,
            Arguments = arguments,
            VmId = vmId,
            Prefix = options.GetValueOrDefault("prefix"),
            Columns = columns,
            Rows = rows,
        };
        IVmConsole console = await WithImageProgressAsync(context, user, vmId, () => Manager.RunProgramAsync(user, run, cancellationToken).AsTask()).ConfigureAwait(false);
        return await RelayAsync(context, console, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The arguments of <c>x86 run</c>: options before the program only (<c>--vm</c>, <c>--prefix</c>,
    /// <c>--cols</c>, <c>--rows</c>, as <c>--name value</c> or <c>--name=value</c>; <c>--</c> ends
    /// them); everything after the program goes to it untouched, options included.
    /// </summary>
    internal static bool TryParseRun(IReadOnlyList<string> args, out Dictionary<string, string> options, out string? program, out List<string> arguments, out string? error)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        arguments = [];
        program = null;
        error = null;
        int index = 0;
        for (; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg == "--")
            {
                index++;
                break;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            string name = arg[2..];
            string? value = null;
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }

            if (!RunOptions.Contains(name))
            {
                error = $"unknown option --{name}";
                return false;
            }

            if (value is null)
            {
                if (index + 1 >= args.Count)
                {
                    error = $"--{name} needs a value";
                    return false;
                }

                value = args[++index];
            }

            options[name] = value;
        }

        if (index >= args.Count)
        {
            error = "run needs a program";
            return false;
        }

        program = args[index];
        arguments.AddRange(args.Skip(index + 1));
        return true;
    }
}
