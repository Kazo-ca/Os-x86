namespace OsX86.Engine;

/// <summary>What the engine boots: a Linux kernel with its initramfs, or a disk with its own boot sector (DOS).</summary>
public enum EngineBootKind
{
    /// <summary>A kernel and an initramfs given to v86 directly (bzImage), with a command line.</summary>
    Linux = 0,

    /// <summary>The BIOS boots the first disk (FreeDOS, trial E14).</summary>
    Dos = 1,
}

/// <summary>
/// Where the bytes of a kernel, an initramfs or a disk are in the origin private file system (OPFS):
/// a whole file, a range of a file (the logical file of an image payload, <c>ImageStore.Locate</c>),
/// or a range stored as a zstd "seekable" stream (frames of <see cref="BlockSize"/> decompressed
/// bytes and a seek table, the form of <c>images/tools/pack-image.py</c>) that the worker
/// decompresses frame by frame.
/// </summary>
/// <param name="Path">The OPFS path, relative to the root, with <c>/</c> separators.</param>
/// <param name="Offset">Where the range starts in the file; null with <paramref name="Size"/> for the whole file.</param>
/// <param name="Size">The length of the range in the file (compressed when <paramref name="RawSize"/> is set).</param>
/// <param name="RawSize">The decompressed length, for a seekable zstd range.</param>
/// <param name="BlockSize">The decompressed size of every frame but the last, for a seekable zstd range.</param>
public sealed record EngineFileSource(string Path, long? Offset = null, long? Size = null, long? RawSize = null, int? BlockSize = null)
{
    /// <summary>The largest frame a seekable stream may have (<c>pack-image.py</c> refuses more).</summary>
    public const int MaxBlockSize = 1 << 30;

    /// <summary>A whole uncompressed file.</summary>
    public static EngineFileSource WholeFile(string path) => new(path);

    /// <summary>An uncompressed range of a file.</summary>
    public static EngineFileSource Range(string path, long offset, long size) => new(path, offset, size);

    /// <summary>A range of a file stored as a seekable zstd stream.</summary>
    public static EngineFileSource Seekable(string path, long offset, long size, long rawSize, int blockSize) => new(path, offset, size, rawSize, blockSize);

    /// <summary>Whether the range is a seekable zstd stream.</summary>
    public bool IsCompressed => RawSize is not null;

    /// <summary>Checks the source; throws <see cref="ArgumentException"/> naming <paramref name="name"/>.</summary>
    public void Validate(string name)
    {
        OpfsPath.Validate(Path, name);
        if (Offset is null != Size is null)
        {
            throw new ArgumentException("Give both the offset and the size of a range, or neither for the whole file.", name);
        }

        if (Offset < 0 || Size < 0)
        {
            throw new ArgumentOutOfRangeException(name, "The offset and the size of a range cannot be negative.");
        }

        if (RawSize is null != BlockSize is null)
        {
            throw new ArgumentException("A seekable zstd range needs both its decompressed size and its block size.", name);
        }

        if (IsCompressed)
        {
            if (Offset is null)
            {
                throw new ArgumentException("A seekable zstd range needs its offset and size.", name);
            }

            if (RawSize < 0 || BlockSize is <= 0 or > MaxBlockSize)
            {
                throw new ArgumentOutOfRangeException(name, $"The decompressed size cannot be negative and the block size must be between 1 and {MaxBlockSize}.");
            }
        }
    }
}

/// <summary>How the writes of the guest to a disk are kept.</summary>
public enum EngineWriteLayerMode
{
    /// <summary>In memory: nothing is written anywhere, everything is gone when the engine stops (tests, VMs without state).</summary>
    Discard = 0,

    /// <summary>In a copy-on-write layer file of the OPFS, flushed every 5 s and at the stop (decision 177 A).</summary>
    Persistent = 1,
}

/// <summary>
/// The copy-on-write layer under a disk: the published image is never modified (plan section 13),
/// the blocks the guest writes go to the layer. A persistent layer is one OPFS file with a header of
/// format 1 and a block map, plus its versioned metadata next to it (<c>&lt;path&gt;.meta.json</c>,
/// <see cref="Storage.StoredObjectMetadata"/> of kind <c>layer</c>): reopened only by the same image
/// version and the same major version of the engine (decision 169), one writer at a time (E11).
/// </summary>
public sealed record EngineWriteLayer
{
    private EngineWriteLayer(EngineWriteLayerMode mode, string? path, string? metadataJson)
    {
        Mode = mode;
        Path = path;
        MetadataJson = metadataJson;
    }

    /// <summary>The in-memory layer.</summary>
    public static EngineWriteLayer Discard { get; } = new(EngineWriteLayerMode.Discard, null, null);

    /// <summary>A persistent layer at <paramref name="path"/>, with the metadata to write when it is created and to compare when it is reopened.</summary>
    public static EngineWriteLayer Persistent(string path, string metadataJson)
    {
        OpfsPath.Validate(path, nameof(path));
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataJson);
        return new EngineWriteLayer(EngineWriteLayerMode.Persistent, path, metadataJson);
    }

    /// <summary>Where the writes go.</summary>
    public EngineWriteLayerMode Mode { get; }

    /// <summary>The OPFS path of the layer file (persistent only).</summary>
    public string? Path { get; }

    /// <summary>The versioned metadata of the layer, as JSON (persistent only).</summary>
    public string? MetadataJson { get; }
}

/// <summary>A disk of the guest: the image bytes it starts from and the layer that keeps its writes.</summary>
/// <param name="Source">The bytes of the disk.</param>
/// <param name="Layer">Where the writes go.</param>
public sealed record EngineDiskOptions(EngineFileSource Source, EngineWriteLayer Layer)
{
    /// <summary>The disks the engine knows: the two IDE disks of v86.</summary>
    public static IReadOnlyList<string> Names { get; } = ["hda", "hdb"];
}

/// <summary>The OPFS paths the engine accepts: relative, <c>/</c> separators, no <c>.</c>, <c>..</c> or empty segment.</summary>
internal static class OpfsPath
{
    public static void Validate(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An OPFS path is required.", name);
        }

        if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"'{path}' is not a relative OPFS path (segments separated by '/', no '.', '..' or empty segment).", name);
        }
    }
}
