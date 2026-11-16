using OsX86.Interface;
using OsX86.Storage;

namespace OsX86.Vm;

/// <summary>
/// The boot image source of a host with a store (work item 6b): an image version complete in the
/// OPFS boots from there; a missing or partial one is downloaded first through the storage worker
/// of work item 3 (every chunk verified, a cut download resumed), from the origin the catalog
/// configures for it, the progress handed to the caller. The boot files are then located in the
/// payload by <see cref="ImageStore.Locate"/> (<see cref="BootImage(ImageManifest)"/>). The write
/// layers of the VMs live in the same store. One download at a time: two VMs that start on a
/// missing image wait for the same one.
/// </summary>
public sealed class StoreBootImageSource : IBootImageSource
{
    private readonly ImageStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the source.</summary>
    /// <param name="store">The image repository of the host.</param>
    /// <param name="catalog">Where the images can be downloaded from.</param>
    public StoreBootImageSource(ImageStore store, ImageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        _store = store;
        Catalog = catalog;
    }

    /// <summary>The images this source can download.</summary>
    public ImageCatalog Catalog { get; }

    /// <inheritdoc />
    public async ValueTask<BootImage> ResolveAsync(string image, string version, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!StorageLayout.IsValidImageId(image) || !StorageLayout.IsValidVersion(version))
        {
            throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"'{image} {version}' is not an image identifier and version.");
        }

        try
        {
            // An image already there needs no gate: only a download waits for another one.
            ImageManifest? manifest = await FindAsync(image, version, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    manifest = await FindAsync(image, version, cancellationToken).ConfigureAwait(false)
                        ?? await DownloadAsync(image, version, progress, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }

            return new BootImage(manifest);
        }
        catch (StorageException exception)
        {
            throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The image {image} {version} is not available ({exception.Code}): {exception.Message}", exception);
        }
    }

    /// <summary>The manifest of a complete stored version, <see langword="null"/> when it must be downloaded.</summary>
    private async Task<ImageManifest?> FindAsync(string image, string version, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredImage> stored = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        StoredImage? present = stored.FirstOrDefault(entry => entry.Image == image && entry.Version == version);
        if (present is { Complete: true, IsSupported: true })
        {
            return await _store.ReadManifestAsync(image, version, cancellationToken).ConfigureAwait(false);
        }

        if (present is { Complete: true, IsSupported: false })
        {
            throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The image {image} {version} in the store has format {present.Format}, which this version of Os-x86 cannot open (decision 169): delete it to download it again.");
        }

        return null;
    }

    private async Task<ImageManifest> DownloadAsync(string image, string version, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ImageSource source = Catalog.SourceOf(image, version)
            ?? throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The image {image} {version} is not in the store and no origin is configured for it: it cannot be downloaded on this host.");
        StoredImage downloaded = await _store.DownloadAsync(source, progress, cancellationToken).ConfigureAwait(false);
        if (downloaded.Image != image || downloaded.Version != version)
        {
            throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The origin configured for {image} {version} serves {downloaded.Image} {downloaded.Version}: the catalog is wrong.");
        }

        return await _store.ReadManifestAsync(image, version, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteLayersAsync(string layerSet, CancellationToken cancellationToken)
    {
        try
        {
            await _store.DeleteLayersAsync(layerSet, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException exception) when (exception.Code == StorageErrorCodes.Locked)
        {
            throw new X86Exception(X86ErrorCodes.VmBusy, $"The disk of the virtual machine is still in use: {exception.Message}", exception);
        }
        catch (StorageException exception)
        {
            throw new X86Exception(X86ErrorCodes.StorageFailed, $"The disk of the virtual machine could not be deleted ({exception.Code}): {exception.Message}", exception);
        }
    }
}
