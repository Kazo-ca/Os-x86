namespace OsX86.Storage;

/// <summary>The error codes of the storage layer; the storage worker protocol uses the same strings.</summary>
public static class StorageErrorCodes
{
    /// <summary>The origin private file system is not available where the worker runs.</summary>
    public const string OpfsUnavailable = "opfs-unavailable";

    /// <summary>The manifest could not be fetched (network, HTTP status, too large).</summary>
    public const string ManifestFetchFailed = "manifest-fetch-failed";

    /// <summary>The SHA-256 of the fetched manifest is not the expected one (decision 203 A).</summary>
    public const string ManifestIntegrity = "manifest-integrity";

    /// <summary>The manifest is not a valid image manifest (schema, sizes, hashes).</summary>
    public const string ManifestInvalid = "manifest-invalid";

    /// <summary>The format number is not one this version reads (decision 169: refused, nothing written).</summary>
    public const string FormatUnsupported = "format-unsupported";

    /// <summary>A chunk did not hash to the value of the manifest after the retries.</summary>
    public const string ChunkIntegrity = "chunk-integrity";

    /// <summary>The network failed for longer than the retries allow.</summary>
    public const string Network = "network";

    /// <summary>The server answered a status that is not worth retrying (404, 403, ...).</summary>
    public const string Http = "http";

    /// <summary>A write to the OPFS failed.</summary>
    public const string WriteFailed = "write-failed";

    /// <summary>The browser refused the write for lack of storage quota.</summary>
    public const string QuotaExceeded = "quota-exceeded";

    /// <summary>Another writer holds the lock of that image (decision E11: one writer per file).</summary>
    public const string Locked = "locked";

    /// <summary>The image or file does not exist in the repository.</summary>
    public const string NotFound = "not-found";

    /// <summary>The operation was cancelled by the caller.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A message did not follow the worker protocol.</summary>
    public const string Protocol = "protocol";

    /// <summary>The worker threw or crashed.</summary>
    public const string WorkerError = "worker-error";

    /// <summary>The operation was abandoned because the store was disposed.</summary>
    public const string Disposed = "disposed";
}

/// <summary>An error of the storage layer, with the code of <see cref="StorageErrorCodes"/> that names it.</summary>
public sealed class StorageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of <see cref="StorageErrorCodes"/>.</param>
    /// <param name="message">What happened, for logs and the user.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public StorageException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    /// <summary>The error code, one of <see cref="StorageErrorCodes"/>.</summary>
    public string Code { get; }
}
