using System.Globalization;
using System.Text;
using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Feature;

public sealed partial class X86Command
{
    /// <summary>A line made of these two characters sends Ctrl+C to the guest (the line-mode stand-in until ADR 0252).</summary>
    internal const string InterruptLine = "^C";

    /// <summary>A line made of these two characters closes the console (as <c>~.</c> closes an ssh session).</summary>
    internal const string CloseLine = "~.";

    /// <summary>How long the last output of a console that ended may take to arrive.</summary>
    internal static readonly TimeSpan OutputDrain = TimeSpan.FromSeconds(2);

    private static readonly byte[] EndOfInput = [0x04];

    private async ValueTask<int> ShellAsync(TerminalExecutionContext context, Guid user, List<string> args, CancellationToken cancellationToken)
    {
        CommandArguments? parsed = CommandArguments.Parse(args, ["vm", "prefix", "cols", "rows"], NoOptions, out string? error);
        if (parsed is null || parsed.Positional.Count != 0)
        {
            return await UsageErrorAsync(context, error ?? "shell takes no positional argument (use --vm)", cancellationToken).ConfigureAwait(false);
        }

        if (!TryParseSize(parsed.Value("cols"), 80, out int columns) || !TryParseSize(parsed.Value("rows"), 25, out int rows))
        {
            return await UsageErrorAsync(context, "--cols and --rows are whole numbers from 1 to 1000", cancellationToken).ConfigureAwait(false);
        }

        Guid vmId;
        if (parsed.Value("vm") is { } named)
        {
            vmId = (await Manager.ResolveAsync(user, named, cancellationToken).ConfigureAwait(false)).Id;
        }
        else
        {
            IReadOnlyList<VmSummary> vms = await Manager.ListAsync(user, cancellationToken).ConfigureAwait(false);
            if (vms.Count == 0)
            {
                throw new X86Exception(X86ErrorCodes.VmNotFound, "You have no virtual machine: create one with `x86 vm create <name>`.");
            }

            if (vms.Count > 1)
            {
                return await UsageErrorAsync(context, "you have several virtual machines: name one with --vm", cancellationToken).ConfigureAwait(false);
            }

            vmId = vms[0].Id;
        }

        IVmConsole console = await Manager.OpenConsoleAsync(user, vmId, new VmConsoleOptions { Prefix = parsed.Value("prefix"), Columns = columns, Rows = rows }, cancellationToken).ConfigureAwait(false);
        return await RelayAsync(context, console, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays a console to the terminal line by line until its program ends: what the guest writes
    /// goes to standard output as text (<see cref="TerminalTextFilter"/>); each line read from standard
    /// input goes to the guest with LF; a line <c>^C</c> and the command's own interruption (Ctrl+C in
    /// the Kazo terminal) send Ctrl+C to the guest program (216 A); a line <c>~.</c> closes the console;
    /// the end of input sends Ctrl+D. Returns the exit code of the console's program; the console is
    /// closed and its VM released on return (166 A).
    /// </summary>
    internal static async Task<int> RelayAsync(TerminalExecutionContext context, IVmConsole console, CancellationToken cancellationToken)
    {
        await using (console.ConfigureAwait(false))
        {
            var filter = new TerminalTextFilter();
            var filterLock = new Lock();
            using var stop = new CancellationTokenSource();
            using CancellationTokenRegistration interrupt = cancellationToken.Register(() => _ = SendQuietlyAsync(() => console.SendInterruptAsync(CancellationToken.None)));
            Task output = PumpOutputAsync(console, filter, filterLock, context.StandardOutput, stop.Token);
            Task input = PumpInputAsync(console, filter, filterLock, context.StandardInput, stop.Token);
            int code;
            try
            {
                code = await console.Exited.ConfigureAwait(false);
            }
            catch (X86Exception exception)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(Quietly(output), Quietly(input)).ConfigureAwait(false);
                await context.StandardError.WriteLineAsync($"x86: {exception.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
                return X86ExitCodes.Failed;
            }

            await Task.WhenAny(output, Task.Delay(OutputDrain, CancellationToken.None)).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quietly(output), Quietly(input)).ConfigureAwait(false);
            string rest;
            lock (filterLock)
            {
                rest = filter.Flush();
            }

            if (rest.Length > 0)
            {
                await context.StandardOutput.WriteAsync(rest.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            await context.StandardOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return code;
        }
    }

    private static async Task PumpOutputAsync(IVmConsole console, TerminalTextFilter filter, Lock filterLock, TextWriter output, CancellationToken stop)
    {
        await foreach (ReadOnlyMemory<byte> chunk in console.ReadOutputAsync(stop).ConfigureAwait(false))
        {
            string text;
            lock (filterLock)
            {
                text = filter.Push(chunk.Span);
            }

            if (text.Length > 0)
            {
                await output.WriteAsync(text.AsMemory(), stop).ConfigureAwait(false);
                await output.FlushAsync(stop).ConfigureAwait(false);
            }
        }
    }

    private static async Task PumpInputAsync(IVmConsole console, TerminalTextFilter filter, Lock filterLock, TextReader input, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(stop).ConfigureAwait(false);
            if (line is null)
            {
                // End of input: the shell gets Ctrl+D (end of file at the start of a line); a serial console has no program of its own to end, so it is closed.
                if (console.IsSerial)
                {
                    await console.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await SendQuietlyAsync(() => console.WriteAsync(EndOfInput, stop)).ConfigureAwait(false);
                }

                return;
            }

            switch (line)
            {
                case InterruptLine:
                    await SendQuietlyAsync(() => console.SendInterruptAsync(stop)).ConfigureAwait(false);
                    break;
                case CloseLine:
                    await console.DisposeAsync().ConfigureAwait(false);
                    return;
                default:
                    lock (filterLock)
                    {
                        filter.ExpectEcho(line);
                    }

                    await SendQuietlyAsync(() => console.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), stop)).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Writes to a console that may have closed meanwhile: its end is reported by <see cref="IVmConsole.Exited"/>, not here.</summary>
    private static async Task SendQuietlyAsync(Func<ValueTask> send)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (X86Exception)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (X86Exception)
        {
        }
    }

    private static bool TryParseSize(string? text, int fallback, out int value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= 1000;
    }
}
