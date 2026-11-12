namespace OsX86.Storage;

/// <summary>
/// The link between <see cref="ImageStore"/> and the Web Worker that downloads, verifies and
/// stores images: a channel that carries the JSON messages of the storage protocol (see
/// <c>wwwroot/storage/storage-worker.js</c>) in both directions. <see cref="JsStorageWorkerChannel"/>
/// is the real one (JS interop through the host module); tests provide their own.
/// </summary>
public interface IStorageWorkerChannel : IAsyncDisposable
{
    /// <summary>Starts the worker; <paramref name="onMessage"/> receives every message it posts, as JSON.</summary>
    ValueTask StartAsync(Func<string, ValueTask> onMessage, CancellationToken cancellationToken);

    /// <summary>Sends a JSON command to the worker.</summary>
    ValueTask PostAsync(string json, CancellationToken cancellationToken);
}
