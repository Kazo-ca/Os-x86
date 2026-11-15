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
/// <c>profile list|show|import|export</c>; the other verbs come with their work items. Every
/// subcommand acts for the user running it (decision 190 A: the stable identifier Kazo gives).
/// </summary>
public sealed partial class X86Command : TerminalAppBase
{
    private readonly X86CommandServices _services;

    /// <summary>
    /// Where the command gets its services. The OS builds an app from its manifest only (KOSAPP0031:
    /// no service injected through the constructor), so they come from this factory; tests replace it.
    /// Work item 8 registers the Feature's own instance in the OS instead.
    /// </summary>
    internal static Func<X86CommandServices> ServicesFactory { get; set; } = static () => X86Runtime.Services;

    /// <summary>Creates the command for the manifest the OS loaded; the OS starts an app through this constructor.</summary>
    /// <param name="manifest">This app's manifest.</param>
    public X86Command(AppManifest manifest)
        : base(manifest)
    {
        _services = ServicesFactory();
    }

    /// <summary>Creates the command with the manifest built from <see cref="X86FeatureApp"/>, for unit tests.</summary>
    public X86Command()
        : this(AppDefinitionMapper.ToManifest(new X86FeatureApp()))
    {
    }

    private VmManager Manager => _services.Manager;

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
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

        if (args[0] is not ("vm" or "shell" or "profile"))
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
            return args[0] switch
            {
                "vm" => await VmAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                "shell" => await ShellAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
                _ => await ProfileAsync(context, user, rest, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (X86Exception exception)
        {
            return await FailAsync(context, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The exit code of an error of Os-x86.</summary>
    internal static int ExitCodeOf(X86Exception exception) => exception.Code switch
    {
        X86ErrorCodes.VmNotFound => X86ExitCodes.NotFound,
        X86ErrorCodes.VmBusy => X86ExitCodes.Busy,
        X86ErrorCodes.MemoryBudgetExceeded => X86ExitCodes.MemoryBudget,
        X86ErrorCodes.EngineUnavailable or X86ErrorCodes.ImageUnavailable or X86ErrorCodes.NoConsole => X86ExitCodes.Unavailable,
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
        "  vm create <name> [--profile <p>]       Create a virtual machine from a profile (default: alpine-test)",
        "  vm start <vm>                          Start a virtual machine and keep it running",
        "  vm stop <vm> [--force]                 Stop a virtual machine (--force: even with applications open)",
        "  vm remove <vm> [--force]               Remove a virtual machine (--force: stop it first)",
        "  vm rename <vm> <name>                  Rename a virtual machine",
        "  vm info <vm>                           Show a virtual machine",
        "  shell [--vm <vm>] [--prefix <p>]       Open a shell in a virtual machine (started when needed)",
        "  profile list                           List the profiles",
        "  profile show <profile>                 Print a profile",
        "  profile import <file>                  Import a .x86profile file",
        "  profile export <profile> <file>        Write a profile to a .x86profile file",
        "<vm> is a name or an identifier (GUID).",
    ], cancellationToken);
}
