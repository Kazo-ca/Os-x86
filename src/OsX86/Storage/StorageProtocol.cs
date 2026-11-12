using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OsX86.Storage;

/// <summary>
/// A message posted by the storage worker (<c>wwwroot/storage/storage-worker.js</c>). Every
/// field but <see cref="Type"/> depends on the type; <see cref="Id"/> names the request the
/// message answers, except for <c>ready</c> and for an error of the worker itself.
/// </summary>
internal sealed class StorageMessage
{
    /// <summary><c>ready</c>, <c>manifest</c>, <c>state</c>, <c>progress</c>, <c>done</c>, <c>images</c>, <c>estimate</c>, <c>verified</c> or <c>error</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The request this message answers.</summary>
    public int? Id { get; set; }

    /// <summary><c>error</c>: one of <see cref="StorageErrorCodes"/>.</summary>
    public string? Code { get; set; }

    /// <summary><c>error</c>: the description.</summary>
    public string? Message { get; set; }

    /// <summary><c>ready</c>: whether the OPFS with sync access handles exists in the worker.</summary>
    public bool Opfs { get; set; }

    /// <summary><c>ready</c>: whether the Web Locks API exists in the worker.</summary>
    public bool Locks { get; set; }

    /// <summary><c>manifest</c>: the manifest text.</summary>
    public string? Text { get; set; }

    /// <summary><c>state</c>: the resume state, or null when nothing is stored.</summary>
    public DownloadStateSnapshot? State { get; set; }

    /// <summary><c>progress</c>: bytes verified so far.</summary>
    public long BytesVerified { get; set; }

    /// <summary><c>progress</c>: the payload size.</summary>
    public long BytesTotal { get; set; }

    /// <summary><c>progress</c>, <c>verified</c>: chunks verified so far.</summary>
    public int ChunksVerified { get; set; }

    /// <summary><c>progress</c>, <c>verified</c>: how many chunks the payload has.</summary>
    public int ChunksTotal { get; set; }

    /// <summary><c>progress</c>: the receive rate over the last seconds.</summary>
    public double BytesPerSecond { get; set; }

    /// <summary><c>images</c>: the image versions found.</summary>
    public List<StoredImageEntry>? Images { get; set; }

    /// <summary><c>images</c>, <c>estimate</c>: the bytes of the repository.</summary>
    public long RepositoryBytes { get; set; }

    /// <summary><c>estimate</c>: bytes used by the origin.</summary>
    public long Usage { get; set; }

    /// <summary><c>estimate</c>: bytes the origin may use.</summary>
    public long Quota { get; set; }

    /// <summary><c>verified</c>: the chunks that do not hash to the manifest.</summary>
    public List<int>? Bad { get; set; }

    /// <summary><c>done</c> of a download: what the version directory occupies.</summary>
    public long BytesOnDisk { get; set; }
}

/// <summary>An image version as the worker lists it (<c>images</c> message).</summary>
internal sealed class StoredImageEntry
{
    public string Image { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public long Size { get; set; }

    public long BytesOnDisk { get; set; }

    public bool Complete { get; set; }

    /// <summary>The format of the metadata, 0 when there is no readable metadata.</summary>
    public int Format { get; set; }

    public Dictionary<string, string>? Engine { get; set; }
}

/// <summary>A command without arguments beyond the id: <c>list</c>, <c>estimate</c>.</summary>
internal sealed class SimpleStorageCommand(string type, int id)
{
    public string Type { get; } = type;

    public int Id { get; } = id;
}

/// <summary>The <c>fetch-manifest</c> command: fetch the manifest and compare its hash.</summary>
internal sealed class FetchManifestCommand
{
    public string Type { get; } = "fetch-manifest";

    public required int Id { get; init; }

    public required string Url { get; init; }

    public required string Sha256 { get; init; }
}

/// <summary>A command on one image version: <c>read-state</c>, <c>read-manifest</c>, <c>delete</c>.</summary>
internal sealed class ImageCommand(string type, int id, string image, string version)
{
    public string Type { get; } = type;

    public int Id { get; } = id;

    public string Image { get; } = image;

    public string Version { get; } = version;
}

/// <summary>The <c>download</c> command: fetch, verify and write the listed chunks.</summary>
internal sealed class DownloadCommand
{
    public string Type { get; } = "download";

    public required int Id { get; init; }

    public required string Image { get; init; }

    public required string Version { get; init; }

    public required string BaseUrl { get; init; }

    /// <summary>The manifest text, stored as the local copy.</summary>
    public required string Manifest { get; init; }

    /// <summary>The metadata text, stored next to the payload (marked complete by the worker at the end).</summary>
    public required string Metadata { get; init; }

    /// <summary>The chunks to fetch (<see cref="ResumePlan.ChunksToFetch"/>).</summary>
    public required IReadOnlyList<int> Chunks { get; init; }

    /// <summary>Whether to discard the previous state and payload first.</summary>
    public bool Reset { get; init; }

    public int Parallelism { get; init; }
}

/// <summary>The <c>verify</c> command: re-read and hash every chunk of a stored image.</summary>
internal sealed class VerifyCommand
{
    public string Type { get; } = "verify";

    public required int Id { get; init; }

    public required string Image { get; init; }

    public required string Version { get; init; }

    public required string Manifest { get; init; }
}

/// <summary>The <c>cancel</c> command: end the download <see cref="Target"/>.</summary>
internal sealed class CancelCommand
{
    public string Type { get; } = "cancel";

    public required int Id { get; init; }

    public required int Target { get; init; }
}

/// <summary>Source-generated JSON for the storage layer (the library is trimmed in Kazo OS).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StorageMessage))]
[JsonSerializable(typeof(StoredObjectMetadata))]
[JsonSerializable(typeof(SimpleStorageCommand))]
[JsonSerializable(typeof(FetchManifestCommand))]
[JsonSerializable(typeof(ImageCommand))]
[JsonSerializable(typeof(DownloadCommand))]
[JsonSerializable(typeof(VerifyCommand))]
[JsonSerializable(typeof(CancelCommand))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}

/// <summary>Serializes the commands and parses the messages of the storage worker protocol.</summary>
internal static class StorageProtocol
{
    /// <summary>The message types the worker posts.</summary>
    public static class Types
    {
        public const string Ready = "ready";
        public const string Manifest = "manifest";
        public const string State = "state";
        public const string Progress = "progress";
        public const string Done = "done";
        public const string Images = "images";
        public const string Estimate = "estimate";
        public const string Verified = "verified";
        public const string Error = "error";
    }

    public static string List(int id) => Serialize(new SimpleStorageCommand("list", id), StorageJsonContext.Default.SimpleStorageCommand);

    public static string Estimate(int id) => Serialize(new SimpleStorageCommand("estimate", id), StorageJsonContext.Default.SimpleStorageCommand);

    public static string FetchManifest(int id, string url, string sha256) =>
        Serialize(new FetchManifestCommand { Id = id, Url = url, Sha256 = sha256 }, StorageJsonContext.Default.FetchManifestCommand);

    public static string ReadState(int id, string image, string version) => Serialize(new ImageCommand("read-state", id, image, version), StorageJsonContext.Default.ImageCommand);

    public static string ReadManifest(int id, string image, string version) => Serialize(new ImageCommand("read-manifest", id, image, version), StorageJsonContext.Default.ImageCommand);

    public static string Delete(int id, string image, string version) => Serialize(new ImageCommand("delete", id, image, version), StorageJsonContext.Default.ImageCommand);

    public static string Download(DownloadCommand command) => Serialize(command, StorageJsonContext.Default.DownloadCommand);

    public static string Verify(VerifyCommand command) => Serialize(command, StorageJsonContext.Default.VerifyCommand);

    public static string Cancel(int id, int target) => Serialize(new CancelCommand { Id = id, Target = target }, StorageJsonContext.Default.CancelCommand);

    /// <summary>Parses a message posted by the worker.</summary>
    /// <exception cref="StorageException">The text is not a message of the protocol (<see cref="StorageErrorCodes.Protocol"/>).</exception>
    public static StorageMessage Parse(string json)
    {
        StorageMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, StorageJsonContext.Default.StorageMessage);
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.Protocol, $"The storage worker sent a message that is not JSON: {exception.Message}", exception);
        }

        if (message is null || string.IsNullOrEmpty(message.Type))
        {
            throw new StorageException(StorageErrorCodes.Protocol, "The storage worker sent a message without a type.");
        }

        return message;
    }

    private static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);
}
