using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Feature;

/// <content>Work item 11: <c>x86 run</c> of a DOS program, shown in the terminal (214 A to 217 A).</content>
public sealed partial class X86Command
{
    /// <summary>The environment variable that makes <c>x86 run</c> print the refresh measures of a DOS screen on standard error.</summary>
    internal const string DosStatisticsVariable = "OSX86_DOS_STATS";

    /// <summary>What <c>x86 run</c> says when a DOS program switches to a graphical mode and BoxedWine is not there (164 A, 217 A).</summary>
    internal const string GraphicsNotice = "the DOS program switched to a graphical mode ({0}x{1}, {2} bits per pixel). Graphical DOS programs are shown by BoxedWine (decisions 164 A and 217 A), which is not installed: the program and its virtual machine {3} were stopped.";

    /// <summary>What <c>x86 run</c> says when the terminal has no full screen to show a DOS program on.</summary>
    internal const string NoFullScreen = "a DOS program needs the full screen of Kazo's Terminal (it is not a stream of lines): run it from the Terminal, not from a script or a pipe.";

    private async ValueTask<int> RunDosAsync(TerminalExecutionContext context, Guid user, Dictionary<string, string> options, string program, byte[] content, List<string> arguments, CancellationToken cancellationToken)
    {
        if (options.ContainsKey("prefix") || options.ContainsKey("cols") || options.ContainsKey("rows"))
        {
            return await UsageErrorAsync(context, "--prefix, --cols and --rows are for Windows console programs: a DOS program runs in a DOS virtual machine, at the size of its own screen", cancellationToken).ConfigureAwait(false);
        }

        if (context.FullScreen is not { } fullScreen)
        {
            await context.StandardError.WriteLineAsync($"x86: {NoFullScreen}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return X86ExitCodes.Unavailable;
        }

        Guid vmId;
        if (options.TryGetValue("vm", out string? named))
        {
            vmId = (await Manager.ResolveAsync(user, named, cancellationToken).ConfigureAwait(false)).Id;
        }
        else
        {
            (VmRecord chosen, bool created) = await Manager.ChooseDosVmAsync(user, cancellationToken).ConfigureAwait(false);
            vmId = chosen.Id;
            if (created)
            {
                await context.StandardError.WriteLineAsync($"x86: created the virtual machine {chosen.Name} ({chosen.Id}) for DOS programs, from the profile {chosen.Profile.Name}.".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        var run = new DosRunOptions { FileName = program, Content = content, Arguments = arguments, VmId = vmId };
        DosProgramSession session = await WithImageProgressAsync(context, user, vmId, () => Manager.RunDosProgramAsync(user, run, cancellationToken).AsTask()).ConfigureAwait(false);
        DosBridgeOutcome outcome;
        DosTerminalBridge bridge;
        try
        {
            var terminal = new KazoFullScreenTerminal(fullScreen, $"DOS program {Path.GetFileName(program)} in the virtual machine {session.VmName}");
            bridge = new DosTerminalBridge(session, terminal, TimeProvider.System);
            outcome = await bridge.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return InterruptedExitCode;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (context.Environment.TryGetValue(DosStatisticsVariable, out string? statistics) && statistics == "1")
        {
            await context.StandardError.WriteLineAsync(bridge.Statistics.Summary().AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }

        if (outcome.Graphics is { } graphics)
        {
            string notice = string.Format(System.Globalization.CultureInfo.InvariantCulture, GraphicsNotice, graphics.Columns, graphics.Rows, graphics.BitsPerPixel, session.VmName);
            await context.StandardError.WriteLineAsync($"x86: {notice}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return X86ExitCodes.Unavailable;
        }

        if (outcome.Failure is { } failure)
        {
            return await FailAsync(context, failure, CancellationToken.None).ConfigureAwait(false);
        }

        // The alternate screen is gone with the program: its last screen stays in the scrollback.
        await WriteLinesAsync(context.StandardOutput, LastScreenLines(bridge.LastScreen), CancellationToken.None).ConfigureAwait(false);
        return outcome.ExitCode ?? X86ExitCodes.Failed;
    }

    /// <summary>The rows of the program's last screen, trailing blanks and blank rows at the end removed.</summary>
    internal static IEnumerable<string> LastScreenLines(Engine.EngineTextScreen? screen)
    {
        if (screen is null)
        {
            return [];
        }

        List<string> rows = [.. screen.Lines.Select(static line => line.TrimEnd())];
        while (rows.Count > 0 && rows[^1].Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }
}
