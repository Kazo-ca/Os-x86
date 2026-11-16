using OsX86.Engine;
using OsX86.Interface;
using OsX86.Storage;

namespace OsX86.Vm;

/// <summary>Creates one engine per running VM (plan §7: the engine is the emulator behind a VM). Tests give a simulated engine.</summary>
public interface IEngineFactory
{
    /// <summary>A new, stopped engine.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.EngineUnavailable"/>: this host cannot run an emulator.</exception>
    IEngine Create();
}

/// <summary>An engine factory for a host without emulator: the command run where no engine can live (ADR 0070 of Kazo; work item 8 gives the Feature one).</summary>
public sealed class UnavailableEngineFactory : IEngineFactory
{
    /// <summary>The only instance.</summary>
    public static UnavailableEngineFactory Instance { get; } = new();

    /// <inheritdoc />
    public IEngine Create() =>
        throw new X86Exception(X86ErrorCodes.EngineUnavailable, "This host cannot start a virtual machine: no emulator is available to the command (the Feature of work item 8 will run it).");
}

/// <summary>
/// What the engine boots for an image (plan §1.1: images in the OPFS): either whole files of the
/// OPFS (work item 2: a kernel, an initramfs and a command line, what the tests copy there), or a
/// stored image (work item 6b): its manifest, from which <see cref="ImageBoot.CreateStartOptions"/>
/// reads the kernel, the initramfs and the disks at their place in <c>image.bin</c>.
/// </summary>
public sealed record BootImage
{
    /// <summary>Whole files of the OPFS (work item 2).</summary>
    /// <param name="kernelPath">OPFS path of the kernel.</param>
    /// <param name="initrdPath">OPFS path of the initramfs.</param>
    /// <param name="kernelCommandLine">The kernel command line.</param>
    public BootImage(string kernelPath, string initrdPath, string kernelCommandLine)
    {
        ArgumentException.ThrowIfNullOrEmpty(kernelPath);
        ArgumentException.ThrowIfNullOrEmpty(initrdPath);
        ArgumentNullException.ThrowIfNull(kernelCommandLine);
        KernelPath = kernelPath;
        InitrdPath = initrdPath;
        KernelCommandLine = kernelCommandLine;
        Files = new Dictionary<string, ImageFileLocation>(StringComparer.Ordinal);
    }

    /// <summary>
    /// A stored image (work item 6b): the logical files it boots from are located in its payload by
    /// <see cref="ImageStore.Locate"/> (the kernel and the initramfs of a Linux image, the disks of
    /// <c>boot.disks</c>).
    /// </summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.NotFound"/>: the manifest names a boot file it does not have.</exception>
    public BootImage(ImageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Manifest = manifest;
        var files = new Dictionary<string, ImageFileLocation>(StringComparer.Ordinal);
        if (manifest.Boot is { Kind: "linux" })
        {
            files[ImageBoot.KernelFile] = ImageStore.Locate(manifest, ImageBoot.KernelFile);
            files[ImageBoot.InitramfsFile] = ImageStore.Locate(manifest, ImageBoot.InitramfsFile);
        }

        foreach ((string disk, string file) in manifest.Boot?.Disks ?? new Dictionary<string, string>())
        {
            files[disk] = ImageStore.Locate(manifest, file);
        }

        Files = files;
        KernelCommandLine = manifest.Boot?.KernelCommandLine;
    }

    /// <summary>OPFS path of a kernel read whole; <see langword="null"/> for a stored image.</summary>
    public string? KernelPath { get; }

    /// <summary>OPFS path of an initramfs read whole; <see langword="null"/> for a stored image.</summary>
    public string? InitrdPath { get; }

    /// <summary>The kernel command line (the manifest's for a stored image, when it has one).</summary>
    public string? KernelCommandLine { get; }

    /// <summary>The manifest of a stored image; <see langword="null"/> for whole files.</summary>
    public ImageManifest? Manifest { get; }

    /// <summary>
    /// Where the boot files of a stored image are in its payload, by role: <c>kernel</c>,
    /// <c>initramfs</c> and each disk (<c>hda</c>); empty for whole files.
    /// </summary>
    public IReadOnlyDictionary<string, ImageFileLocation> Files { get; }

    /// <summary>Whether the image has disks, whose writes go to the layer of the VM.</summary>
    public bool HasDisks => Manifest?.Boot?.Disks.Count > 0;

    /// <summary>
    /// The engine options of one run of a VM: the profile's memory and, for a stored image, its
    /// disks on the VM's persistent write layer (<paramref name="layerSet"/>, one per VM, work item 6b).
    /// </summary>
    /// <exception cref="EngineException"><see cref="EngineErrorCodes.EngineVersionMismatch"/> or <see cref="EngineErrorCodes.BootUnsupported"/> (stored image).</exception>
    public EngineStartOptions CreateStartOptions(int memoryMiB, string layerSet)
    {
        ArgumentException.ThrowIfNullOrEmpty(layerSet);
        if (Manifest is null)
        {
            return new EngineStartOptions
            {
                KernelPath = KernelPath,
                InitrdPath = InitrdPath,
                KernelCommandLine = KernelCommandLine ?? EngineStartOptions.DefaultKernelCommandLine,
                MemoryMiB = memoryMiB,
                VirtioConsole = true,
            };
        }

        return ImageBoot.CreateStartOptions(Manifest, new ImageBootOptions
        {
            LayerMode = EngineWriteLayerMode.Persistent,
            LayerSet = layerSet,
            MemoryMiB = memoryMiB,
        });
    }
}

/// <summary>
/// Finds the boot files of an image version, downloading it when the source can (work item 6b),
/// and keeps the write layers of the VMs (one layer set per VM, deleted with it).
/// </summary>
public interface IBootImageSource
{
    /// <summary>The boot files of <paramref name="image"/> at <paramref name="version"/>, downloaded first when missing and the source knows where from.</summary>
    /// <param name="image">The image identifier.</param>
    /// <param name="version">The image version.</param>
    /// <param name="progress">Receives the progress of a download; nothing when the image is already there.</param>
    /// <param name="cancellationToken">Cancels the download (the verified chunks are kept for a resume).</param>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ImageUnavailable"/>: the image is not in the store and cannot be downloaded, its download failed, or it cannot be booted by this version.</exception>
    ValueTask<BootImage> ResolveAsync(string image, string version, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Deletes the write layers of a VM (its disks start again from the image); nothing when it has none.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmBusy"/> while an engine still runs on them, <see cref="X86ErrorCodes.StartFailed"/> for another failure of the store.</exception>
    ValueTask DeleteLayersAsync(string layerSet, CancellationToken cancellationToken);
}

/// <summary>A boot image source with a fixed table: tests, and the host of the guest tests. An image it does not know is unavailable; it keeps no layer.</summary>
public sealed class StaticBootImageSource : IBootImageSource
{
    private readonly Dictionary<(string Image, string Version), BootImage> _images = [];

    /// <summary>A source that knows no image.</summary>
    public static StaticBootImageSource Empty { get; } = new();

    /// <summary>Registers an image version.</summary>
    public StaticBootImageSource Add(string image, string version, BootImage boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        _images[(image, version)] = boot;
        return this;
    }

    /// <inheritdoc />
    public ValueTask<BootImage> ResolveAsync(string image, string version, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _images.TryGetValue((image, version), out BootImage? boot)
            ? ValueTask.FromResult(boot)
            : throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The image {image} {version} is not available on this host.");
    }

    /// <inheritdoc />
    public ValueTask DeleteLayersAsync(string layerSet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
