using OsX86.Dos;
using OsX86.Engine;
using OsX86.Interface;

namespace OsX86.Vm;

/// <summary>
/// One DOS program running in its VM (work item 11): its text screen read on demand (214 A, 218 A),
/// keys sent to it as scancodes (215 A, 216 A), its start and its end told by the batch of the program
/// disk on COM1 (<see cref="DosRunScript"/>), and the switch to a graphical mode (217 A). It is the
/// application that holds the VM (166 A): disposing it stops the VM as soon as nothing else holds it.
/// </summary>
public sealed class DosProgramSession : IAsyncDisposable
{
    private readonly VmInstance _instance;
    private readonly IEngine _engine;
    private readonly ITextModeEngine _text;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _keys = new(1, 1);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ScreenSizeChangedEventArgs> _graphics = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _markers;
    private readonly Lock _mode = new();
    private ScreenSizeChangedEventArgs? _lastMode;
    private VmApplication? _application;
    private int _disposed;

    internal DosProgramSession(VmInstance instance, IEngine engine, TimeProvider time)
    {
        _instance = instance;
        _engine = engine;
        _time = time;
        _text = engine as ITextModeEngine
            ?? throw new X86Exception(X86ErrorCodes.EngineUnavailable, "This engine cannot show a text screen nor take keys: DOS programs need the v86 engine.");
        VmId = instance.Id;
        VmName = instance.Record.Name;
        _ = _started.Task.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        _ = _exited.Task.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        engine.ScreenSizeChanged += OnScreenSizeChanged;
        _markers = ReadMarkersAsync(engine.GetConsole(ConsoleId.Serial0), _stop.Token);
    }

    /// <summary>The VM the program runs in.</summary>
    public Guid VmId { get; }

    /// <summary>The name of that VM.</summary>
    public string VmName { get; }

    /// <summary>Completes when the program starts (FreeDOS booted and ran the batch); fails when the VM stops first.</summary>
    public Task Started => _started.Task;

    /// <summary>Completes with the program's exit code (its <c>ERRORLEVEL</c>, 0 to 255); fails when the VM stops first.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>Completes when the program switches the VGA to a graphical mode (217 A: <c>bpp ≠ 0</c>).</summary>
    public Task<ScreenSizeChangedEventArgs> GraphicsModeEntered => _graphics.Task;

    /// <summary>The text screen as it is now.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ConsoleClosed"/> when the VM stopped.</exception>
    public async ValueTask<EngineTextScreen> ReadScreenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _text.ReadTextScreenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is EngineException or ObjectDisposedException)
        {
            throw new X86Exception(X86ErrorCodes.ConsoleClosed, $"The virtual machine {VmName} stopped: {exception.Message}", exception);
        }
    }

    /// <summary>Sends one key to the program (keys sent together keep their order).</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ConsoleClosed"/> when the VM stopped.</exception>
    public async ValueTask SendKeyAsync(GuestKey key, CancellationToken cancellationToken)
    {
        IReadOnlyList<ScancodeStep> steps = GuestKeyboard.Translate(key);
        if (steps.Count == 0)
        {
            return;
        }

        await _keys.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (ScancodeStep step in steps)
            {
                await _text.SendScancodesAsync(step.Scancodes, cancellationToken).ConfigureAwait(false);
                if (step.PauseAfter > TimeSpan.Zero)
                {
                    await Task.Delay(step.PauseAfter, _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is EngineException or ObjectDisposedException)
        {
            throw new X86Exception(X86ErrorCodes.ConsoleClosed, $"The virtual machine {VmName} stopped: {exception.Message}", exception);
        }
        finally
        {
            _keys.Release();
        }
    }

    /// <summary>Ends the session: the VM is released and stopped at once when nothing else holds it (166 A).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        VmApplication? application = _application;
        await StopReadingAsync().ConfigureAwait(false);
        if (application is not null)
        {
            application.Dispose();
            await _instance.StopIdleRunAsync(application.Generation, "DOS program ended").ConfigureAwait(false);
        }
    }

    internal void Attach(VmApplication application) => _application = application;

    /// <summary>The start failed: the readers stop, nothing is held.</summary>
    internal async ValueTask AbandonAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopReadingAsync().ConfigureAwait(false);
        }
    }

    private async Task StopReadingAsync()
    {
        _engine.ScreenSizeChanged -= OnScreenSizeChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _markers.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The reader was told to stop.
        }

        var closed = new X86Exception(X86ErrorCodes.ConsoleClosed, $"The DOS program of {VmName} was closed.");
        _started.TrySetException(closed);
        _exited.TrySetException(closed);
        _stop.Dispose();
    }

    private async Task ReadMarkersAsync(IEngineConsole serial, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string line in serial.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DosRunScript.IsStart(line))
                {
                    MarkStarted();
                }
                else if (DosRunScript.ParseExit(line) is { } code)
                {
                    MarkStarted();
                    _exited.TrySetResult(code);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Fail($"its serial port failed: {exception.Message}");
            return;
        }

        // The console ended without the exit line: the engine stopped under the program.
        Fail("the virtual machine stopped before the program ended");
    }

    private void Fail(string why)
    {
        var stopped = new X86Exception(X86ErrorCodes.ConsoleClosed, $"The DOS program of {VmName} did not end normally: {why}.");
        _started.TrySetException(stopped);
        _exited.TrySetException(stopped);
    }

    /// <summary>
    /// The start line arrived. The serial port is flushed every few milliseconds while the mode change
    /// is posted at once: a program that switches to a graphical mode right away can be seen switching
    /// before its start line, so the last mode is looked at again here.
    /// </summary>
    private void MarkStarted()
    {
        lock (_mode)
        {
            _started.TrySetResult();
            if (_lastMode is { IsText: false } mode && !_exited.Task.IsCompleted)
            {
                _graphics.TrySetResult(mode);
            }
        }
    }

    private void OnScreenSizeChanged(object? sender, ScreenSizeChangedEventArgs e)
    {
        lock (_mode)
        {
            // The BIOS and FreeDOS set text modes while they boot (the batch clears the screen in text
            // mode before its start line); the program's own switch counts, and the last mode is kept.
            _lastMode = e;
            if (!e.IsText && _started.Task.IsCompletedSuccessfully && !_exited.Task.IsCompleted)
            {
                _graphics.TrySetResult(e);
            }
        }
    }
}
