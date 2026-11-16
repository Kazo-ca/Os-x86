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

    /// <summary>What <c>x86 shell</c> prints when it opens the serial console of a VM (an image without agent, or without a default guest user and no <c>--prefix</c>).</summary>
    internal const string SerialNotice = "x86: this is the serial console of the virtual machine: the guest's root shell, shared with its boot messages. A line ~. closes it.";

    /// <summary>How often, in percent of the image, a download in progress is reported.</summary>
    internal const int ProgressStepPercent = 10;

    /// <summary>
    /// Runs an operation that may start a VM and reports, on standard error, the download of its
    /// image when one happens (work item 6b): when it starts, every <see cref="ProgressStepPercent"/>
    /// percent, and when it ends. Nothing is written when the image is already in the store.
    /// </summary>
    internal async Task<T> WithImageProgressAsync<T>(TerminalExecutionContext context, Guid user, Guid vmId, Func<Task<T>> operation)
    {
        var reporter = new DownloadReporter(context.StandardError);
        void OnProgress(object? sender, VmInstanceEventArgs<Storage.DownloadProgress> e)
        {
            if (e.Instance.OwnerId == user && e.Instance.Id == vmId)
            {
                reporter.Report(e.Instance.Record.Profile, e.Data);
            }
        }

        Manager.ImageProgress += OnProgress;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            Manager.ImageProgress -= OnProgress;
            await reporter.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="WithImageProgressAsync{T}"/>
    internal async Task WithImageProgressAsync(TerminalExecutionContext context, Guid user, Guid vmId, Func<Task> operation) =>
        await WithImageProgressAsync(context, user, vmId, async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

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

        var consoleOptions = new VmConsoleOptions { Prefix = parsed.Value("prefix"), Columns = columns, Rows = rows };
        IVmConsole console = await WithImageProgressAsync(context, user, vmId, () => Manager.OpenConsoleAsync(user, vmId, consoleOptions, cancellationToken).AsTask()).ConfigureAwait(false);
        if (console.IsSerial)
        {
            // Work item 6b: say which console this is (linux-base has no guest user for an agent shell yet).
            await context.StandardError.WriteLineAsync(SerialNotice.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }

        return await RelayAsync(context, console, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays a console to the terminal line by line until its program ends: what the guest writes
    /// goes to standard output as text (<see cref="TerminalTextFilter"/>); each line read from standard
    /// input goes to the guest with LF; a line <c>^C</c> and the command's own interruption (Ctrl+C in
    /// the Kazo terminal) send Ctrl+C to the guest program (216 A); a line <c>~.</c> closes the console;
    /// the end of input sends Ctrl+D. When the command's process itself is stopped (its tab closed,
    /// <see cref="Kazo.Os.AppSdk.IAppExecutionContext.CancellationToken"/>), the console is closed.
    /// Returns the exit code of the console's program; the console is closed and its VM released on
    /// return (166 A).
    /// </summary>
    internal static async Task<int> RelayAsync(TerminalExecutionContext context, IVmConsole console, CancellationToken cancellationToken)
    {
        await using (console.ConfigureAwait(false))
        {
            var filter = new TerminalTextFilter();
            var filterLock = new Lock();
            using var stop = new CancellationTokenSource();
            using CancellationTokenRegistration interrupt = cancellationToken.Register(() => _ = SendQuietlyAsync(() => console.SendInterruptAsync(CancellationToken.None)));
            CancellationToken process = context.App.CancellationToken;
            using CancellationTokenRegistration killed = process.CanBeCanceled && process != cancellationToken
                ? process.Register(() => _ = Quietly(console.DisposeAsync().AsTask()))
                : default;
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
                await EndPumpsAsync(output, input).ConfigureAwait(false);
                await context.StandardError.WriteLineAsync($"x86: {exception.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
                return X86ExitCodes.Failed;
            }

            await Task.WhenAny(output, Task.Delay(OutputDrain, CancellationToken.None)).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await EndPumpsAsync(output, input).ConfigureAwait(false);
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
                // Not cancelled by the stop: text already read is written, even during the final drain.
                await output.WriteAsync(text.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Waits for the output pump (already told to stop) and leaves the input pump: a read on the
    /// terminal's standard input may not honour cancellation, and the shell must not wait for the
    /// user to press Enter once the guest program ended.
    /// </summary>
    private static async Task EndPumpsAsync(Task output, Task input)
    {
        await Quietly(output).ConfigureAwait(false);
        _ = Quietly(input); // observed in the background; whatever it reads next is dropped
        await Task.WhenAny(input, Task.Delay(InputGiveUp, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>How long the input pump is given to notice the end before it is left behind.</summary>
    private static readonly TimeSpan InputGiveUp = TimeSpan.FromMilliseconds(200);

    /// <summary>Writes to a console that may have closed meanwhile: its end is reported by <see cref="IVmConsole.Exited"/>, not here.</summary>
    private static Task SendQuietlyAsync(Func<ValueTask> send) => Quietly(Task.Run(async () => await send().ConfigureAwait(false)));

    /// <summary>
    /// Awaits a pump or a write whose failure is not the command's result: the console's end, and any
    /// error of the VM under it, are reported by <see cref="IVmConsole.Exited"/>; a write to a closed
    /// console or a terminal that went away changes nothing to that.
    /// </summary>
    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // See the summary: reported through Exited, or irrelevant once the console ended.
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

/// <summary>
/// Turns the reports of a download into lines of standard error, written in order by one task:
/// the reports arrive on the thread of the store, the terminal is written asynchronously.
/// </summary>
internal sealed class DownloadReporter(TextWriter output)
{
    private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Lock _lock = new();
    private Task? _pump;
    private int _lastStep = -1;
    private Storage.DownloadPhase _lastPhase = Storage.DownloadPhase.Idle;

    /// <summary>The line of one report, or <see langword="null"/> when it says nothing new.</summary>
    internal string? Format(string image, Storage.DownloadProgress progress)
    {
        lock (_lock)
        {
            Storage.DownloadPhase previous = _lastPhase;
            _lastPhase = progress.Phase;
            switch (progress.Phase)
            {
                case Storage.DownloadPhase.FetchingManifest when previous != Storage.DownloadPhase.FetchingManifest:
                    return $"x86: {image} is not on this machine yet: downloading it.";
                case Storage.DownloadPhase.Downloading:
                    int percent = (int)(progress.Fraction * 100);
                    int step = percent / X86Command.ProgressStepPercent;
                    if (step <= _lastStep)
                    {
                        return null;
                    }

                    _lastStep = step;
                    string rate = progress.BytesPerSecond > 0 ? string.Create(CultureInfo.InvariantCulture, $", {progress.BytesPerSecond / (1024 * 1024):0.0} MiB/s") : string.Empty;
                    return string.Create(CultureInfo.InvariantCulture, $"x86: downloading {image}: {percent} % ({progress.BytesVerified / (1024.0 * 1024):0.0} of {progress.BytesTotal / (1024.0 * 1024):0.0} MiB{rate})");
                case Storage.DownloadPhase.Completed when previous != Storage.DownloadPhase.Completed:
                    return string.Create(CultureInfo.InvariantCulture, $"x86: {image} downloaded and verified ({progress.BytesTotal / (1024.0 * 1024):0.0} MiB).");
                case Storage.DownloadPhase.Failed when previous != Storage.DownloadPhase.Failed:
                    return $"x86: the download of {image} failed; what was verified is kept for the next try.";
                case Storage.DownloadPhase.Cancelled when previous != Storage.DownloadPhase.Cancelled:
                    return $"x86: the download of {image} was interrupted; what was verified is kept for the next try.";
                default:
                    return null;
            }
        }
    }

    /// <summary>Queues the line of a report.</summary>
    public void Report(Profiles.VmProfile profile, Storage.DownloadProgress progress)
    {
        string? line = Format($"{profile.Image} {profile.ImageVersion}", progress);
        if (line is null)
        {
            return;
        }

        lock (_lock)
        {
            // Started before the line is queued, under the lock CompleteAsync reads it with: a report
            // racing the end of the operation is written, never left behind.
            _pump ??= Task.Run(PumpAsync);
        }

        _lines.Writer.TryWrite(line);
    }

    /// <summary>Writes what is queued and stops.</summary>
    public async Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        Task? pump;
        lock (_lock)
        {
            pump = _pump;
        }

        if (pump is not null)
        {
            await pump.ConfigureAwait(false);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (string line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await output.WriteLineAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Whatever the terminal does, the progress never replaces the command's own result.
        }
    }
}
