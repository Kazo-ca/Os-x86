namespace OsX86.Storage;

/// <summary>
/// What the worker found in the OPFS for one image version before a download: the resume state
/// (<c>download.json</c>: which chunks are verified, for which manifest) and whether the
/// metadata says the image is complete. <see langword="null"/> fields mean "no such file".
/// </summary>
/// <param name="ManifestSha256">The SHA-256 of the manifest the verified chunks belong to.</param>
/// <param name="Verified">The indices of the chunks that are downloaded, hashed and flushed.</param>
/// <param name="Complete">Whether the metadata marks the image complete.</param>
public sealed record DownloadStateSnapshot(string? ManifestSha256, IReadOnlyList<int>? Verified, bool Complete);

/// <summary>
/// Decides what a download has to fetch, from the manifest and the state left by a previous
/// attempt (trial E3: after a cut or a page reload, only the chunks not yet verified are
/// fetched). Pure, so the tests cover every case without a browser; <see cref="ImageStore"/>
/// sends the resulting list to the worker, which fetches nothing else.
/// </summary>
public sealed class ResumePlan
{
    private ResumePlan(ImageManifest manifest, IReadOnlyList<int> verified, IReadOnlyList<int> toFetch, bool reset, string reason)
    {
        Manifest = manifest;
        ChunksVerified = verified;
        ChunksToFetch = toFetch;
        Reset = reset;
        Reason = reason;
        BytesVerified = verified.Sum(manifest.ChunkLength);
        BytesToFetch = toFetch.Sum(manifest.ChunkLength);
    }

    /// <summary>The manifest the plan is for.</summary>
    public ImageManifest Manifest { get; }

    /// <summary>The chunks already verified, kept as they are.</summary>
    public IReadOnlyList<int> ChunksVerified { get; }

    /// <summary>The chunks to fetch, in order.</summary>
    public IReadOnlyList<int> ChunksToFetch { get; }

    /// <summary>Whether the previous state must be discarded first (another manifest, or corrupt).</summary>
    public bool Reset { get; }

    /// <summary>Why the plan is what it is, for logs.</summary>
    public string Reason { get; }

    /// <summary>The bytes of the verified chunks.</summary>
    public long BytesVerified { get; }

    /// <summary>The bytes still to fetch.</summary>
    public long BytesToFetch { get; }

    /// <summary>Whether nothing is left to fetch.</summary>
    public bool IsComplete => ChunksToFetch.Count == 0;

    /// <summary>Builds the plan for <paramref name="manifest"/> given <paramref name="state"/> (<see langword="null"/>: nothing stored yet).</summary>
    public static ResumePlan Create(ImageManifest manifest, DownloadStateSnapshot? state)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var all = Enumerable.Range(0, manifest.ChunkCount).ToList();
        if (state is null || (state.Verified is null && !state.Complete))
        {
            return new ResumePlan(manifest, [], all, false, "nothing stored yet");
        }

        if (!string.Equals(state.ManifestSha256, manifest.Sha256, StringComparison.Ordinal))
        {
            return new ResumePlan(manifest, [], all, true, state.ManifestSha256 is null ? "the stored state names no manifest" : "the stored state belongs to another manifest");
        }

        if (state.Complete)
        {
            return new ResumePlan(manifest, all, [], false, "the image is complete");
        }

        var verified = new SortedSet<int>();
        foreach (int index in state.Verified!)
        {
            if (index < 0 || index >= manifest.ChunkCount)
            {
                return new ResumePlan(manifest, [], all, true, $"the stored state names chunk {index}, outside the manifest");
            }

            verified.Add(index);
        }

        List<int> toFetch = all.Where(index => !verified.Contains(index)).ToList();
        return new ResumePlan(manifest, verified.ToList(), toFetch, false, toFetch.Count == 0 ? "every chunk is verified" : $"{verified.Count} of {manifest.ChunkCount} chunks verified");
    }
}
