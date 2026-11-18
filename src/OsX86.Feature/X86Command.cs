using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>The exit codes of <c>x86</c> (stable: scripts test them). <c>x86 shell</c> returns the code of the guest shell instead.</summary>
public static class X86ExitCodes
{
    /// <summary>Done.</summary>
    public const int Ok = 0;

    /// <summary>The operation failed (agent error, invalid profile or name, unsupported format, start failure).</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong: unknown command or option, missing argument.</summary>
    public const int Usage = 2;

    /// <summary>Starting the VM would exceed the memory budget (decision 165 A); the running VMs are listed.</summary>
    public const int MemoryBudget = 3;

    /// <summary>No VM or profile of the user has that name or identifier.</summary>
    public const int NotFound = 4;

    /// <summary>The VM is in use: applications hold it (stop or remove without <c>--force</c>).</summary>
    public const int Busy = 5;

    /// <summary>Nothing on this host can run it: no emulator, image not in the store, no console for an image without agent.</summary>
    public const int Unavailable = 6;
}

/// <summary>
/// The <c>x86</c> command (decision 185 A: <c>vm</c>, <c>shell</c>, <c>run</c>, <c>detect</c>,
/// <c>rules</c>, <c>profile</c>, <c>snapshot</c>; <c>sudo x86 admin</c>). Work item 6 gives it
/// <c>vm list|create|start|stop|remove|rename|info</c>, <c>shell</c> and
/// <c>profile list|show|import|export</c>; work item 10 adds <c>prefix list|create|delete|export</c>,
/// <c>detect</c> and <c>run</c>; work item 11 runs DOS programs; work item 12 adds <c>rules list|allow|deny|ask|remove|log</c>;
/// the other verbs come with their work items. Every
/// subcommand acts for the user running it (decision 190 A: the stable identifier Kazo gives).
/// </summary>
public sealed partial class X86Command : TerminalAppBase
{
    private readonly X86CommandServices _services;

    /// <summary>
    /// Where the command gets its services: the ones the Feature shares in the page (an app may only
    /// be given SDK services and <c>IJSRuntime</c> through its constructor, KOSAPP0031); tests replace it.
    /// </summary>
    internal static Func<X86CommandServices> ServicesFactory { get; set; } = static () => X86Runtime.Services;

    /// <summary>Creates the command for the manifest the OS loaded, on a host that gives it no JS runtime.</summary>
    /// <param name="manifest">This app's manifest.</param>
    public X86Command(AppManifest manifest)
        : base(manifest)
    {
        _services = ServicesFactory();
    }

    /// <summary>
    /// Creates the command as the OS does on the page (ADR 0034 of Kazo: the longest constructor it can
    /// fill): with the JS runtime of the page, it installs the emulator and the storage when the
    /// service child <c>ca.kazo.x86.host</c> has not yet (decision 288 A).
    /// </summary>
    /// <param name="manifest">This app's manifest.</param>
    /// <param name="js">The JS runtime of the page.</param>
    public X86Command(AppManifest manifest, Microsoft.JSInterop.IJSRuntime js)
        : base(manifest)
    {
        X86Host.EnsureInstalled(js);
        _services = ServicesFactory();
    }

    /// <summary>Creates the command with its child manifest built from <see cref="X86FeatureApp"/> (<c>ca.kazo.x86.cli</c>), for unit tests.</summary>
    public X86Command()
        : this(AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp()).Children[0])
    {
    }

    private VmManager Manager => _services.Manager;

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await TellStorageNoticesAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            await WriteUsageAsync(context.StandardOutput, cancellationToken).ConfigureAwait(false);
            return X86ExitCodes.Ok;
        }

        if (args[0] is "--version" or "version")
        {
            await context.StandardOutput.WriteLineAsync($"Os-x86 {X86Service.FeatureVersion}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return X86ExitCodes.Ok;
        }

        if (args[0] == "detect")
        {
            return await DetectAsync(context, [.. args.Skip(1)], cancellationToken).ConfigureAwait(false);
        }

        if (args[0] is not ("vm" or "shell" or "profile" or "prefix" or "run" or "rules"))
        {
            return await UsageErrorAsync(context, $"unknown command: {string.Join(" ", args)}", cancellationToken).ConfigureAwait(false);
        }

        if (!X86Runtime.TryParseUserId(context.App.UserId, out Guid user))
        {
            return await FailAsync(context, new X86Exception(X86ErrorCodes.InvalidUser, "The user running the command has no stable identifier (decision 190 A)."), cancellationToken).ConfigureAwait(false);
        }

        List<string> rest = [.. args.Skip(1)];
        try
        {
            int code = args[0] switch
            {
                "vm" => await VmAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                "shell" => await ShellAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                "prefix" => await PrefixAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                "run" => await RunAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                "rules" => await RulesAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                _ => await ProfileAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
            };
            if (args[0] is "vm" or "shell" or "run")
            {
                // ADR 0250: images downloaded, layers written or deleted; `x86 vm create` declared already.
                bool created = args[0] == "vm" && rest.Count > 0 && rest[0] == "create";
                await KeepStorageAsync(context, gesture: created && code == X86ExitCodes.Ok, cancellationToken).ConfigureAwait(false);
            }

            return code;
        }
        catch (X86Exception exception)
        {
            return await FailAsync(context, exception, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return InterruptedExitCode; // Ctrl+C before a console was open: as SIGINT ends a program
        }
    }

    /// <summary>
    /// Tells the user, once per page and in the language of the OS, that the browser erased the
    /// storage of Os-x86 or withdrew its protection (ADR 0250 of Kazo, decision 289 A).
    /// </summary>
    private async ValueTask TellStorageNoticesAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        foreach (string notice in _services.Storage.TakeNotices(context.App.Storage, context.App.Culture))
        {
            await context.StandardError.WriteLineAsync(notice.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After a command that changed what Os-x86 keeps in the OPFS: declares its bytes to Kazo and,
    /// after <c>x86 vm create</c> (a gesture of the user), asks for persistence and registers the
    /// marker (ADR 0250, 289 A). A failure is reported as a warning: the command itself succeeded.
    /// </summary>
    private async ValueTask KeepStorageAsync(TerminalExecutionContext context, bool gesture, CancellationToken cancellationToken)
    {
        try
        {
            if (gesture)
            {
                await _services.Storage.ProtectAsync(context.App.Storage, _services.StorageTree, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _services.Storage.ReportUsageAsync(context.App.Storage, _services.StorageTree, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is Storage.StorageException or UnauthorizedAccessException or InvalidOperationException)
        {
            await context.StandardError.WriteLineAsync($"x86: warning: the storage of Os-x86 could not be declared to Kazo ({exception.Message}).".AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The exit code of a command interrupted by Ctrl+C before a console relayed it (128 + SIGINT).</summary>
    internal const int InterruptedExitCode = 130;

    /// <summary>The exit code of an error of Os-x86.</summary>
    internal static int ExitCodeOf(X86Exception exception) => exception.Code switch
    {
        X86ErrorCodes.VmNotFound or X86ErrorCodes.PrefixNotFound or X86ErrorCodes.RuleNotFound => X86ExitCodes.NotFound,
        X86ErrorCodes.VmBusy => X86ExitCodes.Busy,
        X86ErrorCodes.MemoryBudgetExceeded => X86ExitCodes.MemoryBudget,
        X86ErrorCodes.EngineUnavailable or X86ErrorCodes.ImageUnavailable or X86ErrorCodes.NoConsole or X86ErrorCodes.ProgramNotSupported or X86ErrorCodes.NetworkUnavailable => X86ExitCodes.Unavailable,
        _ => X86ExitCodes.Failed,
    };

    private static async ValueTask<int> FailAsync(TerminalExecutionContext context, X86Exception exception, CancellationToken cancellationToken)
    {
        TextWriter error = context.StandardError;
        await error.WriteLineAsync($"x86: {exception.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (exception is VmMemoryBudgetExceededException budget)
        {
            await error.WriteLineAsync($"x86: {budget.RunningMiB} MiB of {budget.LimitMiB} MiB are used by running virtual machines.".AsMemory(), cancellationToken).ConfigureAwait(false);
            foreach (VmSummary vm in budget.Running)
            {
                string compatible = budget.Compatible.Contains(vm) ? "  (compatible: same kind and image)" : string.Empty;
                await error.WriteLineAsync($"x86:   running {vm.Name} ({vm.Id}), {vm.MemoryMiB} MiB{compatible}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await error.WriteLineAsync("x86: stop one with `x86 vm stop <vm>`, or use a compatible one.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return ExitCodeOf(exception);
    }

    private static async ValueTask<int> UsageErrorAsync(TerminalExecutionContext context, string message, CancellationToken cancellationToken)
    {
        await context.StandardError.WriteLineAsync($"x86: {message}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await WriteUsageAsync(context.StandardError, cancellationToken).ConfigureAwait(false);
        return X86ExitCodes.Usage;
    }

    private static async ValueTask WriteLinesAsync(TextWriter writer, IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        foreach (string line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask WriteUsageAsync(TextWriter writer, CancellationToken cancellationToken) => WriteLinesAsync(writer,
    [
        "Usage: x86 <command>",
        "  --version                              Print the version of the feature",
        "  vm list                                List your virtual machines",
        "  vm create <name> [--profile <p>]       Create a virtual machine from a profile (default: linux-base)",
        "  vm start <vm>                          Start a virtual machine and keep it running",
        "  vm stop <vm> [--force]                 Stop a virtual machine (--force: even with applications open)",
        "  vm remove <vm> [--force]               Remove a virtual machine (--force: stop it first)",
        "  vm rename <vm> <name>                  Rename a virtual machine",
        "  vm info <vm>                           Show a virtual machine",
        "  shell [--vm <vm>] [--prefix <p>]       Open a shell in a virtual machine (started when needed)",
        "  run [--vm <vm>] [--prefix <p>] <program> [args...]",
        "                                         Run a Windows console program, or a DOS program on the",
        "                                         terminal's full screen; returns its exit code",
        "  detect <file>                          Tell what kind of program a file is (media type)",
        "  prefix list [--vm <vm>]                List the prefixes of a virtual machine",
        "  prefix create <name> [--vm <vm>]       Make a prefix (its own guest user and Wine files)",
        "  prefix delete <name> [--vm <vm>] [--force]",
        "                                         Delete a prefix, its programs and files",
        "  prefix export <name> [--vm <vm>]       Not available yet (comes with disk export)",
        "  rules list [--vm <vm>] [--prefix <p>|--machine]",
        "                                         List your network rules",
        "  rules allow|deny|ask <destination> [--vm <vm>] [--prefix <p>|--machine]",
        "                                         Allow, refuse, or always ask for a destination:",
        "                                         any, <host>, <host>:<port> or *:<port>",
        "  rules remove <id>                      Remove a network rule",
        "  rules log [--vm <vm>] [--prefix <p>|--machine] [--last <n>]",
        "                                         Show the network journal (connections, refusals)",
        "  profile list                           List the profiles",
        "  profile show <profile>                 Print a profile",
        "  profile import <file>                  Import a .x86profile file",
        "  profile export <profile> <file>        Write a profile to a .x86profile file",
        "<vm> is a name or an identifier (GUID). Without --prefix, the virtual machine's default prefix.",
    ], cancellationToken);
}
