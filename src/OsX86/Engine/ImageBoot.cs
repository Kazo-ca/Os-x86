using System.Text.RegularExpressions;
using OsX86.Engine.V86;
using OsX86.Storage;

namespace OsX86.Engine;

/// <summary>How a stored image is started: where the writes of its disks go, and what to change from its manifest.</summary>
public sealed record ImageBootOptions
{
    /// <summary>Where the writes of the disks go (<see cref="EngineWriteLayerMode.Discard"/> by default: nothing is kept).</summary>
    public EngineWriteLayerMode LayerMode { get; init; } = EngineWriteLayerMode.Discard;

    /// <summary>The layer set of a persistent start (<c>osx86/layers/&lt;set&gt;/</c>; a VM identifier, later): required with <see cref="EngineWriteLayerMode.Persistent"/>.</summary>
    public string? LayerSet { get; init; }

    /// <summary>The memory, instead of the manifest's.</summary>
    public int? MemoryMiB { get; init; }

    /// <summary>The kernel command line, instead of the manifest's.</summary>
    public string? KernelCommandLine { get; init; }

    /// <summary>The frame cache bound.</summary>
    public int FrameCacheMiB { get; init; } = EngineStartOptions.DefaultFrameCacheMiB;

    /// <summary>The version of the engine that starts the image (<see cref="V86Engine.V86Version"/>).</summary>
    public string EngineV86Version { get; init; } = V86Engine.V86Version;

    /// <summary>When a new layer is created (its metadata).</summary>
    public DateTimeOffset? Created { get; init; }
}

/// <summary>
/// Turns the manifest of a stored image into engine start options (work item 6a): the kernel and
/// the initramfs as ranges of <c>image.bin</c> (no separate files), the disks of <c>boot.disks</c>
/// on their write layers, the command line and the memory of <c>boot</c>. The image must be built
/// for the same major version of v86 as this engine (decision 169): otherwise it is refused before
/// anything is read or written.
/// </summary>
public static partial class ImageBoot
{
    /// <summary>The logical files a Linux image boots from.</summary>
    public const string KernelFile = "kernel";

    /// <summary>The logical file of the initramfs.</summary>
    public const string InitramfsFile = "initramfs";

    /// <summary>The options that start <paramref name="manifest"/>.</summary>
    /// <exception cref="EngineException"><see cref="EngineErrorCodes.EngineVersionMismatch"/> for an image of another major version of v86, <see cref="EngineErrorCodes.BootUnsupported"/> for a manifest without a boot section the engine can follow.</exception>
    /// <exception cref="ArgumentException">The boot options are invalid (a persistent start without its layer set).</exception>
    public static EngineStartOptions CreateStartOptions(ImageManifest manifest, ImageBootOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new ImageBootOptions();
        EnsureSameMajorVersion(manifest.V86Version, options.EngineV86Version, $"the image {manifest.Image} {manifest.Version}");
        ImageBootSection boot = manifest.Boot ?? throw new EngineException(EngineErrorCodes.BootUnsupported, $"The image {manifest.Image} {manifest.Version} has no boot section: the engine does not know how to start it.");
        if (options.LayerMode == EngineWriteLayerMode.Persistent && options.LayerSet is null)
        {
            throw new ArgumentException("A persistent start needs a layer set.", nameof(options));
        }

        EngineBootKind kind = boot.Kind switch
        {
            "linux" => EngineBootKind.Linux,
            "dos" => EngineBootKind.Dos,
            _ => throw new EngineException(EngineErrorCodes.BootUnsupported, $"The image {manifest.Image} {manifest.Version} boots as '{boot.Kind}', which this engine does not know."),
        };
        string payload = StorageLayout.PayloadPath(manifest.Image, manifest.Version);
        var disks = new Dictionary<string, EngineDiskOptions>(StringComparer.Ordinal);
        foreach ((string disk, string file) in boot.Disks)
        {
            if (!EngineDiskOptions.Names.Contains(disk))
            {
                throw new EngineException(EngineErrorCodes.BootUnsupported, $"The image {manifest.Image} {manifest.Version} asks for the disk '{disk}', which this engine does not have.");
            }

            disks[disk] = new EngineDiskOptions(Source(manifest, payload, file), Layer(manifest, options, disk));
        }

        var start = new EngineStartOptions
        {
            Kind = kind,
            Kernel = kind == EngineBootKind.Linux ? Source(manifest, payload, KernelFile) : null,
            Initrd = kind == EngineBootKind.Linux ? Source(manifest, payload, InitramfsFile) : null,
            Disks = disks,
            KernelCommandLine = options.KernelCommandLine ?? boot.KernelCommandLine ?? EngineStartOptions.DefaultKernelCommandLine,
            MemoryMiB = options.MemoryMiB ?? boot.MemoryMiB ?? 128,
            FrameCacheMiB = options.FrameCacheMiB,
        };
        start.Validate();
        return start;
    }

    /// <summary>
    /// Decision 169: Os-x86 starts the images and layers made for the same major version of its
    /// engine. Semantic versions; before 1.0 the minor version is the major one (0.5.x and 0.6.x differ).
    /// </summary>
    /// <exception cref="EngineException"><see cref="EngineErrorCodes.EngineVersionMismatch"/>, naming both versions.</exception>
    public static void EnsureSameMajorVersion(string built, string running, string what)
    {
        ArgumentException.ThrowIfNullOrEmpty(built);
        ArgumentException.ThrowIfNullOrEmpty(running);
        if (MajorVersion(built) != MajorVersion(running))
        {
            throw new EngineException(EngineErrorCodes.EngineVersionMismatch,
                $"{char.ToUpperInvariant(what[0])}{what[1..]} is built for v86 {built}; this engine is v86 {running}, another major version. It is refused and nothing was read or written: update the image.");
        }
    }

    /// <summary>The major version of a semantic version: <c>2</c> for 2.1.0, <c>0.5</c> for 0.5.473; the whole text when it is not one.</summary>
    public static string MajorVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Match match = SemanticVersion().Match(version);
        if (!match.Success)
        {
            return version;
        }

        return match.Groups[1].Value == "0" ? $"0.{match.Groups[2].Value}" : match.Groups[1].Value;
    }

    private static EngineFileSource Source(ImageManifest manifest, string payload, string file)
    {
        if (!manifest.Files.TryGetValue(file, out ImageFileEntry? entry))
        {
            throw new EngineException(EngineErrorCodes.BootUnsupported, $"The image {manifest.Image} {manifest.Version} has no file '{file}' to boot from.");
        }

        return entry is { RawSize: long rawSize, BlockSize: int blockSize }
            ? EngineFileSource.Seekable(payload, entry.Offset, entry.Size, rawSize, blockSize)
            : EngineFileSource.Range(payload, entry.Offset, entry.Size);
    }

    private static EngineWriteLayer Layer(ImageManifest manifest, ImageBootOptions options, string disk)
    {
        if (options.LayerMode == EngineWriteLayerMode.Discard)
        {
            return EngineWriteLayer.Discard;
        }

        string metadata = StoredObjectMetadata.ForLayer(manifest, options.EngineV86Version, options.Created ?? DateTimeOffset.UtcNow).ToJson();
        return EngineWriteLayer.Persistent(StorageLayout.LayerPath(options.LayerSet!, disk), metadata);
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.")]
    private static partial Regex SemanticVersion();
}
