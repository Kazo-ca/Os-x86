namespace OsX86.Engine;

/// <summary>What one disk did since the engine started.</summary>
/// <param name="Bytes">The size of the disk.</param>
/// <param name="Reads">How many reads the guest made.</param>
/// <param name="BytesRead">The bytes the guest read.</param>
/// <param name="Writes">How many writes the guest made.</param>
/// <param name="BytesWritten">The bytes the guest wrote (to the layer).</param>
/// <param name="CompressedBytesRead">The bytes read from the image payload (compressed frames).</param>
/// <param name="LayerMode">Where the writes go.</param>
/// <param name="LayerBlocksWritten">The 4 KiB blocks the layer holds.</param>
/// <param name="LayerBytes">What the layer occupies (its file for a persistent layer, its memory otherwise).</param>
public sealed record EngineDiskCounters(long Bytes, long Reads, long BytesRead, long Writes, long BytesWritten, long CompressedBytesRead,
    EngineWriteLayerMode LayerMode, long LayerBlocksWritten, long LayerBytes);

/// <summary>The cache of decompressed frames shared by the disks of an engine.</summary>
/// <param name="Bytes">What it holds now.</param>
/// <param name="MaxBytes">Its bound.</param>
/// <param name="Frames">How many frames it holds.</param>
/// <param name="Hits">Frame lookups served from the cache.</param>
/// <param name="Misses">Frame lookups that decompressed a frame.</param>
/// <param name="Evictions">Frames dropped to stay within the bound.</param>
public sealed record EngineFrameCacheCounters(long Bytes, long MaxBytes, int Frames, long Hits, long Misses, long Evictions);

/// <summary>The counters of the disks of a running engine and of its frame cache (null when no disk is open).</summary>
public sealed record EngineDiskStatistics(IReadOnlyDictionary<string, EngineDiskCounters> Disks, EngineFrameCacheCounters? Cache);
