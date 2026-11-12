namespace OsX86.Engine;

/// <summary>
/// The emulator behind a virtual machine (plan section 7: <c>Engine/</c>, the abstraction; <c>Engine/V86/</c>,
/// the v86 implementation in a Web Worker). One engine runs one guest at a time: start it with an
/// image, talk to it through its console ports, pause, resume, stop. Every state change and
/// error is reported as an event; operations that are not allowed in the current state throw
/// <see cref="EngineStateException"/>, failures reported by the worker throw <see cref="EngineException"/>.
/// </summary>
public interface IEngine : IAsyncDisposable
{
    /// <summary>The current execution state.</summary>
    EngineState State { get; }

    /// <summary>Raised on every state change, with the previous state and the worker's reason when it gave one.</summary>
    event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <summary>Raised for every error the worker reports, fatal or not.</summary>
    event EventHandler<EngineErrorEventArgs>? ErrorReported;

    /// <summary>Raised when the guest changes its video mode (text columns and rows, or pixels when graphical).</summary>
    event EventHandler<ScreenSizeChangedEventArgs>? ScreenSizeChanged;

    /// <summary>Boots an image; returns once the guest executes (<see cref="EngineState.Running"/>).</summary>
    /// <exception cref="EngineStateException">The engine is not stopped.</exception>
    /// <exception cref="EngineException">The image could not be read or the emulator could not start.</exception>
    ValueTask StartAsync(EngineStartOptions options, CancellationToken cancellationToken);

    /// <summary>Tears the emulator down; returns once <see cref="EngineState.Stopped"/>. A no-op when already stopped.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Suspends execution; returns once <see cref="EngineState.Paused"/>.</summary>
    /// <exception cref="EngineStateException">The engine is not running.</exception>
    ValueTask PauseAsync(CancellationToken cancellationToken);

    /// <summary>Continues execution; returns once <see cref="EngineState.Running"/>.</summary>
    /// <exception cref="EngineStateException">The engine is not paused.</exception>
    ValueTask ResumeAsync(CancellationToken cancellationToken);

    /// <summary>The console port <paramref name="id"/>; the same object for the lifetime of the engine.</summary>
    /// <exception cref="ArgumentException">The port does not exist.</exception>
    IEngineConsole GetConsole(ConsoleId id);

    /// <summary>
    /// Gives the engine a canvas to draw the guest screen on: a Blazor <c>ElementReference</c> or a JS
    /// object reference to a <c>canvas</c> element, whose control is transferred to the worker as an
    /// OffscreenCanvas (the display bridge; the rendering itself comes with the graphical guests).
    /// </summary>
    ValueTask AttachDisplayAsync(object canvas, CancellationToken cancellationToken);

    /// <summary>Stops drawing on the attached canvas.</summary>
    ValueTask DetachDisplayAsync(CancellationToken cancellationToken);
}

/// <summary>Arguments of <see cref="IEngine.StateChanged"/>.</summary>
public sealed class EngineStateChangedEventArgs(EngineState previous, EngineState current, string? reason) : EventArgs
{
    /// <summary>The state before the change.</summary>
    public EngineState Previous { get; } = previous;

    /// <summary>The state after the change.</summary>
    public EngineState Current { get; } = current;

    /// <summary>Why, when the worker said (<c>stopped</c>, <c>start-failed</c>, <c>emulator-stopped</c>, ...).</summary>
    public string? Reason { get; } = reason;
}

/// <summary>Arguments of <see cref="IEngine.ErrorReported"/>.</summary>
public sealed class EngineErrorEventArgs(string code, string message, bool fatal) : EventArgs
{
    /// <summary>One of <see cref="EngineErrorCodes"/>.</summary>
    public string Code { get; } = code;

    /// <summary>What the worker said.</summary>
    public string Message { get; } = message;

    /// <summary>Whether the engine stopped because of it.</summary>
    public bool Fatal { get; } = fatal;
}

/// <summary>Arguments of <see cref="IEngine.ScreenSizeChanged"/>.</summary>
public sealed class ScreenSizeChangedEventArgs(int columns, int rows, int bitsPerPixel) : EventArgs
{
    /// <summary>Text columns, or pixels of width in a graphical mode.</summary>
    public int Columns { get; } = columns;

    /// <summary>Text rows, or pixels of height in a graphical mode.</summary>
    public int Rows { get; } = rows;

    /// <summary>0 in text mode; the depth of the graphical mode otherwise (the DOS graphical signal of 164 A, trial E14).</summary>
    public int BitsPerPixel { get; } = bitsPerPixel;

    /// <summary>Whether the guest is in a text mode.</summary>
    public bool IsText => BitsPerPixel == 0;
}
