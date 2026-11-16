using System.Globalization;
using OsX86.Dos;
using OsX86.Engine;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>How a DOS program left the terminal.</summary>
/// <param name="ExitCode">Its exit code when it ended.</param>
/// <param name="Graphics">The graphical mode it switched to (217 A), when it did.</param>
/// <param name="Failure">Why it ended otherwise (the VM stopped under it).</param>
internal sealed record DosBridgeOutcome(int? ExitCode, ScreenSizeChangedEventArgs? Graphics, X86Exception? Failure);

/// <summary>
/// Shows a DOS program in the terminal and types into it (work item 11; decisions 214 A, 215 A,
/// 216 A, 217 A): the text screen is read every <see cref="PollInterval"/>, compared row by row and
/// shown only when a row or the cursor changed; the keys of the terminal go through the Escape chord
/// (unless the terminal has Alt and function keys of its own) and reach the program as scancodes,
/// Ctrl+C included. Ends when the program ends, switches to a graphical mode, or the VM stops.
/// </summary>
internal sealed class DosTerminalBridge(DosProgramSession session, IGuestScreenTerminal terminal, TimeProvider time)
{
    /// <summary>The polling period of 214 A (33 ms: E14 measured about 50 ms per refresh for an idle DOS).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>How long the end of a program is awaited once its screen can no longer be read.</summary>
    private static readonly TimeSpan EndWait = TimeSpan.FromSeconds(2);

    private long _lastKey;
    private volatile bool _resized;

    /// <summary>What the refreshes cost, for the measures of the work item.</summary>
    public DosRefreshStatistics Statistics { get; } = new();

    /// <summary>The last screen shown (the program's last screen once it ended).</summary>
    public EngineTextScreen? LastScreen { get; private set; }

    /// <summary>Runs until the program ends; the terminal is given back in every case.</summary>
    public async Task<DosBridgeOutcome> RunAsync(CancellationToken cancellationToken)
    {
        await terminal.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EngineTextScreen starting = StartingScreen(session.VmName);
            await terminal.ShowAsync(starting, [.. Enumerable.Range(0, starting.Rows)], cancellationToken).ConfigureAwait(false);
            using var end = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task keys = PumpKeysAsync(end.Token);
            try
            {
                return await MirrorAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await end.CancelAsync().ConfigureAwait(false);
                try
                {
                    await keys.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The key pump ends with the program (cancelled), or ended before it when the
                    // terminal failed: either way the outcome of the program is what is returned.
                }
            }
        }
        finally
        {
            await terminal.LeaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The screen shown while FreeDOS boots.</summary>
    internal static EngineTextScreen StartingScreen(string vmName)
    {
        string[] lines = [.. Enumerable.Repeat(new string(' ', 80), 25)];
        lines[0] = $"Os-x86: starting the DOS virtual machine {vmName}...".PadRight(80)[..80];
        return new EngineTextScreen(80, 25, lines, null, new EngineTextCursor(0, 0, false));
    }

    private async Task<DosBridgeOutcome> MirrorAsync(CancellationToken cancellationToken)
    {
        while (!session.Started.IsCompleted)
        {
            await Task.WhenAny(session.Started, Task.Delay(PollInterval, time, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (session.Started.Exception?.InnerException is X86Exception notStarted)
        {
            return new DosBridgeOutcome(null, null, notStarted);
        }

        using var timer = new PeriodicTimer(PollInterval, time);
        while (true)
        {
            if (session.GraphicsModeEntered.IsCompletedSuccessfully)
            {
                return new DosBridgeOutcome(null, await session.GraphicsModeEntered.ConfigureAwait(false), null);
            }

            if (session.Exited.IsCompleted)
            {
                return await EndAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (X86Exception failure)
            {
                // The VM went away: its serial reader sees the end a little after the screen does.
                await Task.WhenAny(session.Exited, Task.Delay(EndWait, time, cancellationToken)).ConfigureAwait(false);
                return session.Exited.IsCompleted ? await EndAsync(cancellationToken).ConfigureAwait(false) : new DosBridgeOutcome(null, null, failure);
            }

            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DosBridgeOutcome> EndAsync(CancellationToken cancellationToken)
    {
        if (session.Exited.IsFaulted)
        {
            return new DosBridgeOutcome(null, null, session.Exited.Exception?.InnerException as X86Exception
                ?? new X86Exception(X86ErrorCodes.ConsoleClosed, "The DOS program did not end normally."));
        }

        int code = await session.Exited.ConfigureAwait(false);
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false); // the last screen the program left
        }
        catch (X86Exception)
        {
            // The VM is already going: the last screen read stays.
        }

        return new DosBridgeOutcome(code, null, null);
    }

    /// <summary>One poll: read, compare by row, show what changed (or everything after a resize).</summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        long started = time.GetTimestamp();
        EngineTextScreen screen = await session.ReadScreenAsync(cancellationToken).ConfigureAwait(false);
        double readMs = time.GetElapsedTime(started).TotalMilliseconds;
        // A resize, or a change of the blink mode (it changes the colours of every row), shows all rows.
        bool resized = _resized || (LastScreen is not null && LastScreen.BlinkEnabled != screen.BlinkEnabled);
        _resized = false;
        IReadOnlyList<int> rows = resized ? [.. Enumerable.Range(0, screen.Lines.Count)] : TextScreenDiff.ChangedRows(LastScreen, screen);
        bool cursor = TextScreenDiff.CursorChanged(LastScreen, screen);
        double? renderMs = null;
        double? keyMs = null;
        if (rows.Count > 0 || cursor)
        {
            long shownAt = time.GetTimestamp();
            await terminal.ShowAsync(screen, rows, cancellationToken).ConfigureAwait(false);
            renderMs = time.GetElapsedTime(shownAt).TotalMilliseconds;
            LastScreen = screen;
            long key = Interlocked.Exchange(ref _lastKey, 0);
            if (key != 0 && rows.Count > 0)
            {
                keyMs = time.GetElapsedTime(key).TotalMilliseconds;
            }
        }

        Statistics.Record(readMs, renderMs, rows.Count, keyMs);
    }

    private async Task PumpKeysAsync(CancellationToken cancellationToken)
    {
        var chord = new EscapeChord();
        bool needChord = !terminal.Capabilities.FunctionKeys;
        while (!cancellationToken.IsCancellationRequested)
        {
            GuestTerminalInput input;
            if (chord.IsPending)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(EscapeChord.Timeout);
                try
                {
                    input = await terminal.ReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await SendAsync(chord.Tick(long.MaxValue / 2), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            else
            {
                input = await terminal.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (input.Resized)
            {
                _resized = true;
                continue;
            }

            foreach (GuestKey key in input.Keys)
            {
                long now = (long)time.GetElapsedTime(0).TotalMilliseconds;
                await SendAsync(needChord ? chord.Feed(key, now) : [key], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(IReadOnlyList<GuestKey> keys, CancellationToken cancellationToken)
    {
        if (!session.Started.IsCompletedSuccessfully || session.Exited.IsCompleted)
        {
            return; // typed while FreeDOS boots, or after the end: nobody reads it
        }

        foreach (GuestKey key in keys)
        {
            // From the key the terminal gave to the first frame that changes: the delay the user sees.
            Interlocked.CompareExchange(ref _lastKey, time.GetTimestamp(), 0);
            try
            {
                await session.SendKeyAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return; // the VM or its worker went away: the mirror reports it
            }
        }
    }
}

/// <summary>The cost of the refreshes of a DOS screen (work item 11: the delay per refresh, E14).</summary>
internal sealed class DosRefreshStatistics
{
    private readonly Lock _lock = new();
    private readonly List<double> _reads = [];
    private readonly List<double> _renders = [];
    private readonly List<double> _keys = [];

    /// <summary>How many times the screen was read.</summary>
    public int Polls { get; private set; }

    /// <summary>How many frames were shown.</summary>
    public int Frames { get; private set; }

    /// <summary>How many rows changed in all.</summary>
    public int RowsChanged { get; private set; }

    /// <summary>The read times (ms), the render times of the frames shown (ms), the key-to-screen times (ms).</summary>
    public (double[] Reads, double[] Renders, double[] Keys) Samples
    {
        get
        {
            lock (_lock)
            {
                return ([.. _reads], [.. _renders], [.. _keys]);
            }
        }
    }

    /// <summary>Records one poll.</summary>
    public void Record(double readMs, double? renderMs, int rowsChanged, double? keyMs)
    {
        lock (_lock)
        {
            Polls++;
            _reads.Add(readMs);
            RowsChanged += rowsChanged;
            if (renderMs is { } render)
            {
                Frames++;
                _renders.Add(render);
            }

            if (keyMs is { } key)
            {
                _keys.Add(key);
            }
        }
    }

    /// <summary>One line: counts, then median, 90th percentile and maximum of each measure.</summary>
    public string Summary()
    {
        (double[] reads, double[] renders, double[] keys) = Samples;
        return string.Create(CultureInfo.InvariantCulture, $"x86: DOS screen: {Polls} polls, {Frames} frames, {RowsChanged} rows changed; read {Describe(reads)}; frame {Describe(renders)}; key to screen {Describe(keys)} (median/p90/max ms)");
    }

    /// <summary>Median, 90th percentile and maximum, or a dash without samples.</summary>
    internal static string Describe(double[] samples)
    {
        if (samples.Length == 0)
        {
            return "-";
        }

        double[] sorted = [.. samples.Order()];
        double At(double fraction) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"{At(0.5):0.0}/{At(0.9):0.0}/{sorted[^1]:0.0}");
    }
}
