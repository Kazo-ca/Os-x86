namespace OsX86.Storage;

/// <summary>Where a download stands.</summary>
public enum DownloadPhase
{
    /// <summary>Nothing requested yet.</summary>
    Idle = 0,

    /// <summary>The manifest is being fetched and checked against its expected hash.</summary>
    FetchingManifest = 1,

    /// <summary>The manifest is being validated and the resume state read: nothing is written yet.</summary>
    Planning = 2,

    /// <summary>Chunks are being fetched, hashed and written.</summary>
    Downloading = 3,

    /// <summary>Every chunk is verified and the image is marked complete.</summary>
    Completed = 4,

    /// <summary>The download failed; what is verified stays in the OPFS for a later resume.</summary>
    Failed = 5,

    /// <summary>The download was cancelled; what is verified stays in the OPFS for a later resume.</summary>
    Cancelled = 6,
}

/// <summary>The rules of <see cref="DownloadPhase"/>: which phase may follow which.</summary>
public static class DownloadStateMachine
{
    /// <summary>Whether a download may go from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool CanTransition(DownloadPhase from, DownloadPhase to)
    {
        if (IsFinal(from))
        {
            return false;
        }

        return to switch
        {
            DownloadPhase.FetchingManifest => from == DownloadPhase.Idle,
            DownloadPhase.Planning => from == DownloadPhase.FetchingManifest,
            DownloadPhase.Downloading => from == DownloadPhase.Planning,
            DownloadPhase.Completed => from == DownloadPhase.Downloading,
            DownloadPhase.Failed or DownloadPhase.Cancelled => true,
            _ => false,
        };
    }

    /// <summary>Whether the phase ends the download.</summary>
    public static bool IsFinal(DownloadPhase phase) => phase is DownloadPhase.Completed or DownloadPhase.Failed or DownloadPhase.Cancelled;

    /// <summary>Whether something may have been written to the OPFS in that phase.</summary>
    public static bool MayHaveWritten(DownloadPhase phase) => phase is DownloadPhase.Downloading or DownloadPhase.Completed;
}

/// <summary>The progress of a download, as reported to the caller (decision 203 A: bytes, chunks, speed).</summary>
/// <param name="Phase">Where the download stands.</param>
/// <param name="BytesVerified">The bytes of the chunks verified and written so far (resumed ones included).</param>
/// <param name="BytesTotal">The payload size.</param>
/// <param name="ChunksVerified">How many chunks are verified.</param>
/// <param name="ChunksTotal">How many chunks the payload has.</param>
/// <param name="BytesPerSecond">The receive rate over the last seconds, 0 when idle.</param>
public sealed record DownloadProgress(DownloadPhase Phase, long BytesVerified, long BytesTotal, int ChunksVerified, int ChunksTotal, double BytesPerSecond)
{
    /// <summary>The fraction done, 0 to 1.</summary>
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesVerified / BytesTotal, 0, 1);
}

/// <summary>Where an image is published: the directory that holds <c>manifest.json</c> and the chunks, and the hash the manifest must have.</summary>
/// <param name="BaseUrl">The directory URL, ending with <c>/</c>.</param>
/// <param name="ManifestSha256">The expected SHA-256 of the manifest text, hex (decision 203 A).</param>
/// <param name="Parallelism">How many chunks to fetch at a time, 1 to <see cref="MaxParallelism"/>.</param>
public sealed record ImageSource(string BaseUrl, string ManifestSha256, int Parallelism = ImageSource.DefaultParallelism)
{
    /// <summary>Two chunks at a time: trial E3 measured one stream, a second one covers the latency of the next request.</summary>
    public const int DefaultParallelism = 2;

    /// <summary>The most chunks fetched at a time (one chunk-sized buffer each).</summary>
    public const int MaxParallelism = 4;

    /// <summary>Checks the fields.</summary>
    /// <exception cref="ArgumentException">A field is malformed.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !BaseUrl.EndsWith('/'))
        {
            throw new ArgumentException("The base URL must be an absolute http(s) URL ending with '/'.", nameof(BaseUrl));
        }

        if (!ImageManifest.IsLowercaseSha256(ManifestSha256?.ToLowerInvariant()))
        {
            throw new ArgumentException("The manifest SHA-256 must be 64 hex digits.", nameof(ManifestSha256));
        }

        if (Parallelism is < 1 or > MaxParallelism)
        {
            throw new ArgumentException($"The parallelism must be between 1 and {MaxParallelism}.", nameof(Parallelism));
        }
    }

    /// <summary>The URL of the manifest.</summary>
    public string ManifestUrl => BaseUrl + StorageLayout.ManifestFileName;
}

/// <summary>What the browser reports about the origin's storage (<c>navigator.storage.estimate()</c>).</summary>
/// <param name="Usage">Bytes used by the origin, as the browser counts them.</param>
/// <param name="Quota">Bytes the origin may use.</param>
/// <param name="RepositoryBytes">Bytes of the image repository, as the worker adds them up.</param>
public sealed record StorageEstimate(long Usage, long Quota, long RepositoryBytes);

/// <summary>An image version present in the repository.</summary>
/// <param name="Image">The image identifier.</param>
/// <param name="Version">The image version.</param>
/// <param name="Size">The payload size of the manifest, in bytes.</param>
/// <param name="BytesOnDisk">What the version directory occupies in the OPFS.</param>
/// <param name="Complete">Whether every chunk is verified (the metadata says so).</param>
/// <param name="Format">The format of the metadata (<see cref="StoredObjectMetadata.CurrentFormat"/> when readable).</param>
/// <param name="Engine">The engine versions of the metadata, when readable.</param>
public sealed record StoredImage(string Image, string Version, long Size, long BytesOnDisk, bool Complete, int Format, IReadOnlyDictionary<string, string> Engine)
{
    /// <summary>Whether this version of Os-x86 can open the image (decision 169).</summary>
    public bool IsSupported => Format == StoredObjectMetadata.CurrentFormat;

    /// <summary>The OPFS directory of the image version.</summary>
    public string Directory => StorageLayout.ImageDirectory(Image, Version);

    /// <summary>The OPFS path of the payload, what the engine reads.</summary>
    public string PayloadPath => StorageLayout.PayloadPath(Image, Version);
}

/// <summary>Where a logical file of an image is: the OPFS path of the payload and the range inside it.</summary>
/// <param name="PayloadPath">The OPFS path of the payload.</param>
/// <param name="Offset">Where the file starts in the payload.</param>
/// <param name="Size">Its length.</param>
public sealed record ImageFileLocation(string PayloadPath, long Offset, long Size);

/// <summary>The outcome of an in-place verification of an image (every chunk re-read and hashed).</summary>
/// <param name="ChunksTotal">How many chunks were checked.</param>
/// <param name="BadChunks">The indices of the chunks that do not hash to the manifest (empty when the image is sound).</param>
public sealed record VerificationResult(int ChunksTotal, IReadOnlyList<int> BadChunks)
{
    /// <summary>Whether every chunk is sound.</summary>
    public bool IsSound => BadChunks.Count == 0;
}
