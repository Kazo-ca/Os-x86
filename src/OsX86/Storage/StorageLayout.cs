using System.Text.RegularExpressions;

namespace OsX86.Storage;

/// <summary>
/// Where things live in the origin private file system (OPFS) and what names are allowed there.
/// Every path is relative to the OPFS root, with <c>/</c> as separator, the same strings the
/// engine worker takes in its start options. The worker (<c>wwwroot/storage/storage-worker.js</c>)
/// applies the same rules before it creates a directory.
/// </summary>
public static partial class StorageLayout
{
    /// <summary>The directory of Os-x86 in the OPFS.</summary>
    public const string Root = "osx86";

    /// <summary>The image repository: one directory per image, one per version under it.</summary>
    public const string ImagesDirectory = Root + "/images";

    /// <summary>The copy of the published manifest, in the version directory.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The chunks concatenated: the image payload, in the version directory.</summary>
    public const string PayloadFileName = "image.bin";

    /// <summary>The resume state of a download (which chunks are verified), in the version directory.</summary>
    public const string DownloadStateFileName = "download.json";

    /// <summary>The versioned metadata of the image (<see cref="StoredObjectMetadata"/>), in the version directory.</summary>
    public const string MetadataFileName = "image.meta.json";

    /// <summary>The longest identifier or version accepted.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Whether <paramref name="image"/> is an image identifier: lowercase letters, digits and dashes, 1 to 64 characters.</summary>
    public static bool IsValidImageId(string? image) =>
        image is not null && image.Length <= MaxNameLength && ImageIdPattern().IsMatch(image);

    /// <summary>Whether <paramref name="version"/> is a semantic version (<c>1.2.3</c>, with optional pre-release and build), 1 to 64 characters.</summary>
    public static bool IsValidVersion(string? version) =>
        version is not null && version.Length <= MaxNameLength && VersionPattern().IsMatch(version);

    /// <summary>Whether <paramref name="name"/> is a logical file name of a manifest (<c>kernel</c>, <c>initramfs</c>, <c>rootfs</c>, ...).</summary>
    public static bool IsValidFileName(string? name) =>
        name is not null && name.Length <= MaxNameLength && FileNamePattern().IsMatch(name);

    /// <summary>The OPFS directory of one version of one image.</summary>
    /// <exception cref="ArgumentException">The identifier or the version is not valid.</exception>
    public static string ImageDirectory(string image, string version)
    {
        if (!IsValidImageId(image))
        {
            throw new ArgumentException($"'{image}' is not an image identifier (lowercase letters, digits and dashes).", nameof(image));
        }

        if (!IsValidVersion(version))
        {
            throw new ArgumentException($"'{version}' is not a semantic version.", nameof(version));
        }

        return $"{ImagesDirectory}/{image}/{version}";
    }

    /// <summary>The OPFS path of the payload of one version of one image.</summary>
    public static string PayloadPath(string image, string version) => $"{ImageDirectory(image, version)}/{PayloadFileName}";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex ImageIdPattern();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex FileNamePattern();
}
