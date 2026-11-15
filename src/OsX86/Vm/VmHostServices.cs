using OsX86.Engine;
using OsX86.Interface;

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

/// <summary>What the engine boots for an image (plan §1.1: images in the OPFS). Disks (<c>hda</c>) come with work item 6a.</summary>
/// <param name="KernelPath">OPFS path of the kernel.</param>
/// <param name="InitrdPath">OPFS path of the initramfs.</param>
/// <param name="KernelCommandLine">The kernel command line.</param>
public sealed record BootImage(string KernelPath, string InitrdPath, string KernelCommandLine);

/// <summary>Finds the boot files of an image version in the local store.</summary>
public interface IBootImageSource
{
    /// <summary>The boot files of <paramref name="image"/> at <paramref name="version"/>.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.ImageUnavailable"/>: the image is not in the store, or cannot be booted by this version.</exception>
    ValueTask<BootImage> ResolveAsync(string image, string version, CancellationToken cancellationToken);
}

/// <summary>A boot image source with a fixed table: tests, and the host of the guest tests. An image it does not know is unavailable.</summary>
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
    public ValueTask<BootImage> ResolveAsync(string image, string version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _images.TryGetValue((image, version), out BootImage? boot)
            ? ValueTask.FromResult(boot)
            : throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The image {image} {version} is not available on this host.");
    }
}
