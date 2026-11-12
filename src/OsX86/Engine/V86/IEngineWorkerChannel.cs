namespace OsX86.Engine.V86;

/// <summary>
/// The link between <see cref="V86Engine"/> and the Web Worker that runs v86: a channel that
/// carries the JSON messages of the worker protocol (see <c>wwwroot/engine/v86-worker.js</c>) in
/// both directions. <see cref="JsEngineWorkerChannel"/> is the real one (JS interop through the
/// host module); tests provide their own.
/// </summary>
public interface IEngineWorkerChannel : IAsyncDisposable
{
    /// <summary>Starts the worker; <paramref name="onMessage"/> receives every message it posts, as JSON.</summary>
    ValueTask StartAsync(Func<string, ValueTask> onMessage, CancellationToken cancellationToken);

    /// <summary>Sends a JSON message to the worker.</summary>
    ValueTask PostAsync(string json, CancellationToken cancellationToken);

    /// <summary>Transfers the control of a canvas element to the worker (the display bridge).</summary>
    ValueTask AttachCanvasAsync(object canvas, CancellationToken cancellationToken);

    /// <summary>Tells the worker to stop drawing on the canvas it holds.</summary>
    ValueTask DetachCanvasAsync(CancellationToken cancellationToken);
}
