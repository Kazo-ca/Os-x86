using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsX86.Storage;

/// <summary>
/// One image the app knows where to get (work item 6b): its identifier and version, the address of
/// its published manifest and the SHA-256 that manifest must have (decision 203 A: the hash is
/// configuration, never read from the origin itself).
/// </summary>
/// <param name="Image">The image identifier (<c>linux-base</c>).</param>
/// <param name="Version">The image version.</param>
/// <param name="ManifestUrl">The address of <c>manifest.json</c>; absolute, or relative to the base address the catalog is resolved against.</param>
/// <param name="ManifestSha256">The expected SHA-256 of the manifest text, 64 hex digits.</param>
public sealed record KnownImage(string Image, string Version, string ManifestUrl, string ManifestSha256)
{
    /// <summary>Checks the fields (the address may still be relative).</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.ManifestInvalid"/> naming the first problem.</exception>
    public void Validate()
    {
        if (!StorageLayout.IsValidImageId(Image))
        {
            throw Invalid($"'{Image}' is not an image identifier (lowercase letters, digits and dashes)");
        }

        if (!StorageLayout.IsValidVersion(Version))
        {
            throw Invalid($"the version '{Version}' of {Image} is not a semantic version");
        }

        if (string.IsNullOrWhiteSpace(ManifestUrl)
            || !(ManifestUrl == StorageLayout.ManifestFileName || ManifestUrl.EndsWith("/" + StorageLayout.ManifestFileName, StringComparison.Ordinal))
            || ManifestUrl.StartsWith("//", StringComparison.Ordinal)
            || !Uri.TryCreate(ManifestUrl, UriKind.RelativeOrAbsolute, out _))
        {
            throw Invalid($"the manifest address of {Image} {Version} must end with /{StorageLayout.ManifestFileName} (a relative one may not start with //)");
        }

        if (!ImageManifest.IsLowercaseSha256(ManifestSha256?.ToLowerInvariant()))
        {
            throw Invalid($"the manifest SHA-256 of {Image} {Version} must be 64 hex digits");
        }
    }

    /// <summary>
    /// Where the store downloads it from: the directory of the manifest, made absolute against
    /// <paramref name="baseAddress"/> when the address is relative (a worker resolves a relative
    /// address against its script, not the page: the store always gets an absolute one).
    /// </summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.ManifestInvalid"/>: a relative address without base, or an address the store refuses (not https, http only on the loopback host).</exception>
    public ImageSource ToSource(Uri? baseAddress)
    {
        Validate();
        Uri manifest;
        if (Uri.TryCreate(ManifestUrl, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            manifest = absolute;
        }
        else if (baseAddress is { IsAbsoluteUri: true })
        {
            manifest = new Uri(baseAddress, ManifestUrl);
        }
        else
        {
            throw Invalid($"the manifest address of {Image} {Version} is relative and no base address is known");
        }

        string text = manifest.GetLeftPart(UriPartial.Path);
        var source = new ImageSource(text[..^StorageLayout.ManifestFileName.Length], ManifestSha256.ToLowerInvariant());
        try
        {
            source.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The image catalog is invalid: {Image} {Version}: {exception.Message}", exception);
        }

        return source;
    }

    private static StorageException Invalid(string what) => new(StorageErrorCodes.ManifestInvalid, $"The image catalog is invalid: {what}.");
}

/// <summary>
/// The images the app can download (work item 6b): configuration, never addresses written in the
/// code (the hosting of the images is not settled). The host reads it from its own files; the dev
/// host generates one for the packs built in <c>images/build/</c>. Format 1:
/// <code>
/// {"format": 1, "images": [{"image": "linux-base", "version": "0.1.0",
///   "manifestUrl": "images/build/linux-base/pack/manifest.json", "manifestSha256": "…"}]}
/// </code>
/// Unknown properties are ignored; an unknown format is refused (decision 169).
/// </summary>
public sealed class ImageCatalog
{
    /// <summary>The only format this version reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The largest catalog text accepted, in bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    private readonly Dictionary<(string Image, string Version), KnownImage> _images = [];

    /// <summary>Creates a catalog; one entry per image version.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.ManifestInvalid"/>: an invalid or duplicated entry.</exception>
    /// <exception cref="ArgumentException">The base address is relative.</exception>
    public ImageCatalog(IEnumerable<KnownImage> images, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (baseAddress is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("The base address must be absolute.", nameof(baseAddress));
        }

        BaseAddress = baseAddress;
        foreach (KnownImage image in images)
        {
            if (image is null)
            {
                throw new StorageException(StorageErrorCodes.ManifestInvalid, "The image catalog is invalid: an entry is null.");
            }

            image.Validate();
            if (!_images.TryAdd((image.Image, image.Version), image))
            {
                throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The image catalog is invalid: {image.Image} {image.Version} is listed twice.");
            }
        }
    }

    /// <summary>A catalog without any image: nothing can be downloaded.</summary>
    public static ImageCatalog Empty { get; } = new([]);

    /// <summary>What relative manifest addresses are resolved against (the page of the host).</summary>
    public Uri? BaseAddress { get; }

    /// <summary>The known images, in no particular order.</summary>
    public IReadOnlyCollection<KnownImage> Images => _images.Values;

    /// <summary>The entry of an image version, or <see langword="null"/>.</summary>
    public KnownImage? Find(string image, string version) =>
        _images.TryGetValue((image, version), out KnownImage? known) ? known : null;

    /// <summary>Where the store downloads an image version from, or <see langword="null"/> when the catalog does not list it.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.ManifestInvalid"/>: see <see cref="KnownImage.ToSource"/>.</exception>
    public ImageSource? SourceOf(string image, string version) => Find(image, version)?.ToSource(BaseAddress);

    /// <summary>Reads a catalog text.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.ManifestInvalid"/>, or <see cref="StorageErrorCodes.FormatUnsupported"/> for another format.</exception>
    public static ImageCatalog Parse(string json, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The image catalog is more than the {MaxBytes} bytes accepted.");
        }

        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogDocument);
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The image catalog is not valid JSON: {exception.Message}", exception);
        }

        if (document is null)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, "The image catalog is empty.");
        }

        if (document.Format != CurrentFormat)
        {
            throw new StorageException(StorageErrorCodes.FormatUnsupported, $"The image catalog has format {document.Format}; this version of Os-x86 reads format {CurrentFormat} only.");
        }

        var images = new List<KnownImage>();
        foreach (CatalogEntry? entry in document.Images ?? [])
        {
            if (entry is null)
            {
                throw new StorageException(StorageErrorCodes.ManifestInvalid, "The image catalog is invalid: an entry is null.");
            }

            images.Add(new KnownImage(entry.Image ?? string.Empty, entry.Version ?? string.Empty, entry.ManifestUrl ?? string.Empty, entry.ManifestSha256 ?? string.Empty));
        }

        return new ImageCatalog(images, baseAddress);
    }

    internal sealed class CatalogDocument
    {
        public int Format { get; init; }

        public List<CatalogEntry?>? Images { get; init; }
    }

    internal sealed class CatalogEntry
    {
        public string? Image { get; init; }

        public string? Version { get; init; }

        public string? ManifestUrl { get; init; }

        public string? ManifestSha256 { get; init; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ImageCatalog.CatalogDocument))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
