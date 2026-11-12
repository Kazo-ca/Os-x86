namespace OsX86.Engine;

/// <summary>The error codes an engine reports; the worker protocol uses the same strings.</summary>
public static class EngineErrorCodes
{
    /// <summary>The operation is not allowed in the current state.</summary>
    public const string InvalidState = "invalid-state";

    /// <summary>The origin private file system is not available where the worker runs.</summary>
    public const string OpfsUnavailable = "opfs-unavailable";

    /// <summary>A path of the start options does not exist in the OPFS.</summary>
    public const string ImageNotFound = "image-not-found";

    /// <summary>A file of the image exists but could not be read whole.</summary>
    public const string ImageReadFailed = "image-read-failed";

    /// <summary>The vendored v86 script could not be loaded in the worker.</summary>
    public const string V86LoadFailed = "v86-load-failed";

    /// <summary>v86 refused the configuration or threw while being created.</summary>
    public const string V86InitFailed = "v86-init-failed";

    /// <summary>v86 could not fetch one of its own files (wasm, BIOS).</summary>
    public const string DownloadError = "download-error";

    /// <summary>A message did not follow the worker protocol.</summary>
    public const string Protocol = "protocol";

    /// <summary>The worker threw or crashed.</summary>
    public const string WorkerError = "worker-error";

    /// <summary>The engine reached <see cref="EngineState.Stopped"/> before a pending operation completed.</summary>
    public const string Stopped = "stopped";

    /// <summary>The operation was abandoned because the engine was disposed.</summary>
    public const string Disposed = "disposed";
}

/// <summary>An error reported by an engine, with the code of <see cref="EngineErrorCodes"/> that names it.</summary>
public class EngineException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of <see cref="EngineErrorCodes"/>.</param>
    /// <param name="message">What happened, for logs and the user.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public EngineException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    /// <summary>The error code, one of <see cref="EngineErrorCodes"/>.</summary>
    public string Code { get; }
}

/// <summary>An operation was requested in a state that does not allow it (<see cref="EngineStateMachine.CanRequest"/>).</summary>
public sealed class EngineStateException : EngineException
{
    /// <summary>Creates the exception for <paramref name="operation"/> requested while in <paramref name="state"/>.</summary>
    public EngineStateException(EngineOperation operation, EngineState state)
        : base(EngineErrorCodes.InvalidState, $"Cannot {operation.ToString().ToLowerInvariant()} the engine: it is {state.ToString().ToLowerInvariant()}.")
    {
        Operation = operation;
        State = state;
    }

    /// <summary>Creates the exception with a custom message (another operation in progress, a console write while stopped).</summary>
    public EngineStateException(EngineOperation operation, EngineState state, string message)
        : base(EngineErrorCodes.InvalidState, message)
    {
        Operation = operation;
        State = state;
    }

    /// <summary>The operation that was refused.</summary>
    public EngineOperation Operation { get; }

    /// <summary>The state the engine was in.</summary>
    public EngineState State { get; }
}
