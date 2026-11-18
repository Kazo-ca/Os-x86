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

    /// <summary>
    /// The marker file of ADR 0250 of Kazo (work item 8, decision 289 A): Kazo checks at each boot that
    /// it still exists; when the browser erased the OPFS, the Feature learns it through KazoStorage.
    /// </summary>
    public const string MarkerPath = Root + "/.kazoos-marker";

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

    /// <summary>The write layers of the disks (work item 6a): one directory per layer set (a VM, later), one layer file per disk.</summary>
    public const string LayersDirectory = Root + "/layers";

    /// <summary>The extension of a layer file; its versioned metadata is the same name plus <see cref="LayerMetadataSuffix"/>.</summary>
    public const string LayerExtension = ".layer";

    /// <summary>What follows the name of a layer file to name its metadata.</summary>
    public const string LayerMetadataSuffix = ".meta.json";

    /// <summary>The OPFS directory of a layer set (an identifier like an image's).</summary>
    /// <exception cref="ArgumentException">The name is not an identifier.</exception>
    public static string LayerDirectory(string set)
    {
        if (!IsValidImageId(set))
        {
            throw new ArgumentException($"'{set}' is not a layer set name (lowercase letters, digits and dashes).", nameof(set));
        }

        return $"{LayersDirectory}/{set}";
    }

    /// <summary>The OPFS path of the layer of disk <paramref name="disk"/> (<c>hda</c>) in a layer set.</summary>
    public static string LayerPath(string set, string disk)
    {
        if (!IsValidFileName(disk))
        {
            throw new ArgumentException($"'{disk}' is not a disk name.", nameof(disk));
        }

        return $"{LayerDirectory(set)}/{disk}{LayerExtension}";
    }

    /// <summary>
    /// The state of each user (work item 8, decision 287 A): one directory per stable user identifier
    /// (decision 190 A, 32 hexadecimal digits), next to the disks, so an eviction of the OPFS takes the
    /// registry with the disks it describes. Only the code of Os-x86 separates the users there, as for the layers.
    /// </summary>
    public const string UsersDirectory = Root + "/users";

    /// <summary>The VM registry of a user, with the profiles the user imported (<c>VmRegistryDocument</c>).</summary>
    public const string RegistryFileName = "registry.json";

    /// <summary>The network rules of a user (work item 12).</summary>
    public const string NetworkRulesFileName = "network-rules.json";

    /// <summary>The network journal of a user (work item 12).</summary>
    public const string NetworkJournalFileName = "network-journal.json";

    /// <summary>The largest file of a user the worker reads or writes (the journal may reach 2 MiB).</summary>
    public const int MaxUserFileBytes = 4 * 1024 * 1024;

    /// <summary>Whether <paramref name="name"/> may name a file of a user: lowercase letters, digits and dashes, then <c>.json</c>.</summary>
    public static bool IsValidUserFileName(string? name) =>
        name is not null && name.Length <= MaxNameLength && UserFileNamePattern().IsMatch(name);

    /// <summary>The OPFS path of a file of a user.</summary>
    /// <exception cref="ArgumentException">The user is <see cref="Guid.Empty"/> or the name is not a file of a user.</exception>
    public static string UserFilePath(Guid user, string name)
    {
        CheckUserFile(user, name);
        return $"{UsersDirectory}/{user:N}/{name}";
    }

    /// <summary>Checks a user and a file name before they reach the worker.</summary>
    /// <exception cref="ArgumentException">The user is <see cref="Guid.Empty"/> or the name is not a file of a user.</exception>
    internal static void CheckUserFile(Guid user, string name)
    {
        if (user == Guid.Empty)
        {
            throw new ArgumentException("A user has a stable identifier (decision 190 A); the empty GUID is none.", nameof(user));
        }

        if (!IsValidUserFileName(name))
        {
            throw new ArgumentException($"'{name}' is not the name of a file of a user.", nameof(name));
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]*\.json\z")]
    private static partial Regex UserFileNamePattern();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z")]
    private static partial Regex ImageIdPattern();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*\z")]
    private static partial Regex FileNamePattern();
}
