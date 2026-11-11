using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>
/// The <c>x86</c> command (decision 185 A: <c>vm</c>, <c>shell</c>, <c>run</c>, <c>detect</c>,
/// <c>rules</c>, <c>profile</c>, <c>snapshot</c>; <c>sudo x86 admin</c>). This first version knows
/// <c>--version</c> and <c>vm list</c>; each work item of the milestone adds its verbs.
/// </summary>
public sealed class X86Command : TerminalAppBase
{
    private readonly Ix86 _x86;

    /// <summary>
    /// Where the command gets its service. The OS builds an app from its manifest only (KOSAPP0031:
    /// no service injected through the constructor), so the service comes from this factory; tests
    /// replace it. Work item 8 registers the real instance in the OS instead.
    /// </summary>
    internal static Func<Ix86> ServiceFactory { get; set; } = static () => new X86Service();

    /// <summary>Creates the command for the manifest the OS loaded; the OS starts an app through this constructor.</summary>
    /// <param name="manifest">This app's manifest.</param>
    public X86Command(AppManifest manifest)
        : base(manifest)
    {
        _x86 = ServiceFactory();
    }

    /// <summary>Creates the command with the manifest built from <see cref="X86FeatureApp"/>, for unit tests.</summary>
    public X86Command()
        : this(AppDefinitionMapper.ToManifest(new X86FeatureApp()))
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            await WriteUsageAsync(context.StandardOutput, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        switch (args[0])
        {
            case "--version":
            case "version":
                await context.StandardOutput.WriteLineAsync($"Os-x86 {_x86.Version}".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 0;
            case "vm" when args.Count >= 2 && args[1] == "list":
                return await ListVmsAsync(context, cancellationToken).ConfigureAwait(false);
            default:
                await context.StandardError.WriteLineAsync($"x86: unknown command: {string.Join(" ", args)}".AsMemory(), cancellationToken).ConfigureAwait(false);
                await WriteUsageAsync(context.StandardError, cancellationToken).ConfigureAwait(false);
                return 2;
        }
    }

    private async ValueTask<int> ListVmsAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<VmSummary> vms = await _x86.GetVmsAsync(cancellationToken).ConfigureAwait(false);
        if (vms.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync("No virtual machine.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (VmSummary vm in vms)
        {
            await context.StandardOutput.WriteLineAsync($"{vm.Id}  {vm.Kind,-16} {vm.State,-9} {vm.Name}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static async ValueTask WriteUsageAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync("Usage: x86 <command>".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("  --version      Print the version of the feature".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("  vm list        List your virtual machines".AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
