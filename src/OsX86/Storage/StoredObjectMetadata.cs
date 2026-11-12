using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsX86.Storage;

/// <summary>What a stored object is (plan section 13: disks, layers and restore points come with later milestones).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StoredObjectKind>))]
public enum StoredObjectKind
{
    /// <summary>A downloaded image (the payload of a manifest).</summary>
    [JsonStringEnumMemberName("image")]
    Image = 0,

    /// <summary>A writable disk of a VM.</summary>
    [JsonStringEnumMemberName("disk")]
    Disk = 1,

    /// <summary>A copy-on-write layer over an image or a disk.</summary>
    [JsonStringEnumMemberName("layer")]
    Layer = 2,

    /// <summary>A restore point taken before an update (decision 188).</summary>
    [JsonStringEnumMemberName("restore-point")]
    RestorePoint = 3,
}

/// <summary>
/// The versioned metadata written next to every stored object (plan section 13, decisions D44
/// and D56): <c>image.meta.json</c> next to an image payload today, the sidecar of every disk,
/// layer and restore point later. It says which format the object has, which image version and
/// which engine version produced it, and whether it is complete. Format 1:
/// <code>
/// {"format": 1, "kind": "image", "image": "alpine-test", "imageVersion": "1.0.0",
///  "engine": {"v86": "0.5.473"}, "created": "2026-11-12T10:00:00Z", "complete": false}
/// </code>
/// A format this version does not read is refused and nothing is written (decision 169); the
/// migrations of decision 188 come later and will read the previous major format.
/// </summary>
public sealed class StoredObjectMetadata
{
    /// <summary>The only format this version reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The largest metadata text accepted, in bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The format number (<see cref="CurrentFormat"/>).</summary>
    public int Format { get; init; } = CurrentFormat;

    /// <summary>What the object is.</summary>
    public required StoredObjectKind Kind { get; init; }

    /// <summary>The image the object comes from.</summary>
    public required string Image { get; init; }

    /// <summary>The version of that image.</summary>
    public required string ImageVersion { get; init; }

    /// <summary>The engine versions that produced the object (<c>v86</c> at least).</summary>
    public required IReadOnlyDictionary<string, string> Engine { get; init; }

    /// <summary>When the object was created.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>Whether the object is whole: for an image, every chunk verified.</summary>
    public bool Complete { get; init; }

    /// <summary>Builds the metadata of the image a manifest describes, not complete yet.</summary>
    public static StoredObjectMetadata ForImage(ImageManifest manifest, DateTimeOffset created)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new StoredObjectMetadata
        {
            Kind = StoredObjectKind.Image,
            Image = manifest.Image,
            ImageVersion = manifest.Version,
            Engine = manifest.Engine,
            Created = created,
            Complete = false,
        };
    }

    /// <summary>The JSON text of the metadata, as written in the OPFS.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, StorageJsonContext.Default.StoredObjectMetadata);

    /// <summary>Reads a metadata text; refuses an unknown format or an invalid document.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.FormatUnsupported"/> or <see cref="StorageErrorCodes.ManifestInvalid"/>.</exception>
    public static StoredObjectMetadata Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxBytes)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The metadata is {json.Length} characters, more than the {MaxBytes} accepted.");
        }

        int format = PeekFormat(json);
        if (format != CurrentFormat)
        {
            throw new StorageException(StorageErrorCodes.FormatUnsupported, $"The stored object has format {format}; this version of Os-x86 reads format {CurrentFormat} only. It is refused and nothing was written.");
        }

        StoredObjectMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize(json, StorageJsonContext.Default.StoredObjectMetadata);
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The metadata is invalid: {exception.Message}", exception);
        }

        if (metadata is null || !StorageLayout.IsValidImageId(metadata.Image) || !StorageLayout.IsValidVersion(metadata.ImageVersion) || !metadata.Engine.ContainsKey("v86"))
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, "The metadata is invalid: image, imageVersion or engine.v86 is missing or malformed.");
        }

        return metadata;
    }

    private static int PeekFormat(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("format", out JsonElement element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out int format))
            {
                return format;
            }
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The metadata is not JSON: {exception.Message}", exception);
        }

        throw new StorageException(StorageErrorCodes.ManifestInvalid, "The metadata is invalid: 'format' is missing or not an integer.");
    }
}
