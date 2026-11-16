using System.Reflection;
using OsX86.Storage;

namespace OsX86.Os;

/// <summary>
/// The local origin of the images for the dev host (work item 6b: the hosting of the images is not
/// settled, so no address is written in the code). The build serves every pack built in
/// <c>images/build/&lt;image&gt;/pack/</c> at <c>osx86-images/&lt;image&gt;/pack/</c> and records, as
/// assembly metadata <c>OsX86.DevPack</c>, the directory and the SHA-256 the pack's
/// <c>manifest.sha256</c> gives. At start, each manifest is fetched once and checked against that
/// hash to learn the image and its version; the catalog then lets the store download them like any
/// other origin (hash checked again, every chunk verified). A pack that is missing or does not
/// verify is left out, with a line in the browser console.
/// </summary>
internal static class DevImages
{
    /// <summary>The key of the assembly metadata the build writes: <c>&lt;directory&gt;|&lt;sha256&gt;</c>.</summary>
    internal const string MetadataKey = "OsX86.DevPack";

    /// <summary>Where the dev host serves the packs, relative to its base address.</summary>
    internal const string ServedDirectory = "osx86-images";

    /// <summary>The catalog of the packs the build served; empty when none was built.</summary>
    public static async Task<ImageCatalog> LoadCatalogAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        using var http = new HttpClient { BaseAddress = baseAddress };
        var images = new List<KnownImage>();
        foreach ((string directory, string sha256) in Packs(typeof(DevImages).Assembly))
        {
            string url = $"{ServedDirectory}/{directory}/pack/{StorageLayout.ManifestFileName}";
            try
            {
                byte[] text = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                ImageManifest manifest = ImageManifest.Parse(text, sha256);
                images.Add(new KnownImage(manifest.Image, manifest.Version, url, sha256));
            }
            catch (Exception exception) when (exception is HttpRequestException or StorageException)
            {
                await Console.Error.WriteLineAsync($"Os-x86 dev host: the pack {directory} is left out of the image catalog: {exception.Message}").ConfigureAwait(false);
            }
        }

        return new ImageCatalog(images.DistinctBy(image => (image.Image, image.Version)), baseAddress);
    }

    /// <summary>The packs recorded by the build: directory and expected manifest hash.</summary>
    internal static IEnumerable<(string Directory, string Sha256)> Packs(Assembly assembly)
    {
        foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key != MetadataKey || metadata.Value is not { } value)
            {
                continue;
            }

            string[] parts = value.Split('|');
            string sha256 = parts.Length == 2 ? parts[1].Trim().ToLowerInvariant() : string.Empty;
            if (parts.Length == 2 && StorageLayout.IsValidImageId(parts[0]) && sha256.Length == 64 && sha256.All(Uri.IsHexDigit))
            {
                yield return (parts[0], sha256);
            }
        }
    }
}
