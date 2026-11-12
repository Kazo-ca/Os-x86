using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OsX86.Storage;

/// <summary>A logical file inside the image payload: <c>kernel</c>, <c>initramfs</c>, <c>rootfs</c>, ...</summary>
/// <param name="Offset">Where the file starts in the payload, in bytes.</param>
/// <param name="Size">Its length, in bytes.</param>
public sealed record ImageFileEntry(long Offset, long Size);

/// <summary>
/// The manifest published next to the chunks of an image (<c>manifest.json</c>, the schema shared
/// with the image recipes of work item 4). Format 1:
/// <code>
/// {"format": 1, "image": "alpine-test", "version": "1.0.0", "engine": {"v86": "0.5.473"},
///  "size": 838860800, "chunkSize": 33554432,
///  "chunks": [{"sha256": "&lt;64 lowercase hex&gt;"}, ...],
///  "files": {"kernel": {"offset": 0, "size": 1234}, ...}}
/// </code>
/// The chunks are the files <c>chunk-00000.bin</c>, <c>chunk-00001.bin</c>, ... next to the
/// manifest; the last one is shorter. <see cref="Parse(string)"/> validates everything; a format
/// this version does not read is refused (<see cref="StorageErrorCodes.FormatUnsupported"/>,
/// decision 169) and the manifest fetched for a download must hash to the value the caller
/// expects (<see cref="Parse(ReadOnlySpan{byte}, string)"/>, decision 203 A).
/// </summary>
public sealed class ImageManifest
{
    /// <summary>The only format this version reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The chunk size the recipes use (trial E3: the right size).</summary>
    public const int DefaultChunkSize = 32 * 1024 * 1024;

    /// <summary>The smallest chunk size accepted.</summary>
    public const int MinChunkSize = 64 * 1024;

    /// <summary>The largest chunk size accepted (the worker holds one chunk per slot in memory).</summary>
    public const int MaxChunkSize = 512 * 1024 * 1024;

    /// <summary>The most chunks a manifest may list (five digits in the chunk file names).</summary>
    public const int MaxChunks = 100_000;

    /// <summary>The largest manifest text accepted, in bytes.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    private ImageManifest(string json, string sha256, int format, string image, string version, IReadOnlyDictionary<string, string> engine, long size, int chunkSize, IReadOnlyList<string> chunkHashes, IReadOnlyDictionary<string, ImageFileEntry> files)
    {
        Json = json;
        Sha256 = sha256;
        Format = format;
        Image = image;
        Version = version;
        Engine = engine;
        Size = size;
        ChunkSize = chunkSize;
        ChunkHashes = chunkHashes;
        Files = files;
    }

    /// <summary>The manifest text, as published (what the worker stores as the local copy).</summary>
    public string Json { get; }

    /// <summary>The SHA-256 of <see cref="Json"/> (UTF-8), lowercase hex: the identity of the manifest.</summary>
    public string Sha256 { get; }

    /// <summary>The format number (<see cref="CurrentFormat"/>).</summary>
    public int Format { get; }

    /// <summary>The image identifier (<see cref="StorageLayout.IsValidImageId"/>).</summary>
    public string Image { get; }

    /// <summary>The image version (<see cref="StorageLayout.IsValidVersion"/>).</summary>
    public string Version { get; }

    /// <summary>The engine versions the image was built for (<c>v86</c> at least).</summary>
    public IReadOnlyDictionary<string, string> Engine { get; }

    /// <summary>The payload size, in bytes.</summary>
    public long Size { get; }

    /// <summary>The size of every chunk but the last, in bytes.</summary>
    public int ChunkSize { get; }

    /// <summary>The SHA-256 of each chunk, lowercase hex, in chunk order.</summary>
    public IReadOnlyList<string> ChunkHashes { get; }

    /// <summary>The logical files of the payload.</summary>
    public IReadOnlyDictionary<string, ImageFileEntry> Files { get; }

    /// <summary>How many chunks the payload has.</summary>
    public int ChunkCount => ChunkHashes.Count;

    /// <summary>The v86 version the image was built for.</summary>
    public string V86Version => Engine["v86"];

    /// <summary>The name of the chunk file <paramref name="index"/>: <c>chunk-00007.bin</c>.</summary>
    public static string ChunkFileName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxChunks);
        return $"chunk-{index:D5}.bin";
    }

    /// <summary>The length of chunk <paramref name="index"/> (the last one is shorter).</summary>
    public long ChunkLength(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ChunkCount);
        return Math.Min(ChunkSize, Size - (long)index * ChunkSize);
    }

    /// <summary>Parses and validates a manifest fetched with a known expected hash (decision 203 A).</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.ManifestIntegrity"/> when the bytes do not hash to <paramref name="expectedSha256"/>; otherwise as <see cref="Parse(string)"/>.</exception>
    public static ImageManifest Parse(ReadOnlySpan<byte> utf8, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256);
        if (utf8.Length > MaxManifestBytes)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The manifest is {utf8.Length} bytes, more than the {MaxManifestBytes} accepted.");
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(utf8));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException(StorageErrorCodes.ManifestIntegrity, $"The manifest hashes to {actual}, not to the expected {expectedSha256.ToLowerInvariant()}: the image is refused.");
        }

        return Parse(Encoding.UTF8.GetString(utf8));
    }

    /// <summary>Parses and validates a manifest text.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.FormatUnsupported"/> for a format this version does not read, <see cref="StorageErrorCodes.ManifestInvalid"/> for anything else wrong.</exception>
    public static ImageManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxManifestBytes)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The manifest is {bytes.Length} bytes, more than the {MaxManifestBytes} accepted.");
        }

        using JsonDocument document = ParseDocument(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("the manifest is not a JSON object");
        }

        int format = ReadFormat(root);
        string image = ReadName(root, "image", StorageLayout.IsValidImageId, "an image identifier (lowercase letters, digits and dashes)");
        string version = ReadName(root, "version", StorageLayout.IsValidVersion, "a semantic version");
        IReadOnlyDictionary<string, string> engine = ReadEngine(root);
        long size = ReadInteger(root, "size", 1, long.MaxValue);
        int chunkSize = (int)ReadInteger(root, "chunkSize", MinChunkSize, MaxChunkSize);
        IReadOnlyList<string> chunks = ReadChunks(root, size, chunkSize);
        IReadOnlyDictionary<string, ImageFileEntry> files = ReadFiles(root, size);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ImageManifest(json, sha256, format, image, version, engine, size, chunkSize, chunks, files);
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The manifest is not JSON: {exception.Message}", exception);
        }
    }

    private static StorageException Invalid(string what) => new(StorageErrorCodes.ManifestInvalid, $"The manifest is invalid: {what}.");

    private static JsonElement Require(JsonElement root, string name, JsonValueKind kind, string description)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            throw Invalid($"'{name}' is missing");
        }

        if (element.ValueKind != kind)
        {
            throw Invalid($"'{name}' must be {description}");
        }

        return element;
    }

    private static int ReadFormat(JsonElement root)
    {
        JsonElement element = Require(root, "format", JsonValueKind.Number, "an integer");
        if (!element.TryGetInt32(out int format))
        {
            throw Invalid("'format' must be an integer");
        }

        if (format != CurrentFormat)
        {
            throw new StorageException(StorageErrorCodes.FormatUnsupported, $"The manifest has format {format}; this version of Os-x86 reads format {CurrentFormat} only. The image is refused and nothing was written.");
        }

        return format;
    }

    private static string ReadName(JsonElement root, string name, Func<string?, bool> isValid, string description)
    {
        string? value = Require(root, name, JsonValueKind.String, "a string").GetString();
        if (!isValid(value))
        {
            throw Invalid($"'{name}' must be {description}");
        }

        return value!;
    }

    private static long ReadInteger(JsonElement element, string name, long min, long max)
    {
        JsonElement value = Require(element, name, JsonValueKind.Number, "an integer");
        if (!value.TryGetInt64(out long number) || number < min || number > max)
        {
            throw Invalid($"'{name}' must be an integer between {min} and {max}");
        }

        return number;
    }

    private static IReadOnlyDictionary<string, string> ReadEngine(JsonElement root)
    {
        JsonElement element = Require(root, "engine", JsonValueKind.Object, "an object");
        var engine = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.Value.GetString()))
            {
                throw Invalid($"'engine.{property.Name}' must be a non-empty string");
            }

            engine[property.Name] = property.Value.GetString()!;
        }

        if (!engine.ContainsKey("v86"))
        {
            throw Invalid("'engine.v86' is missing");
        }

        return engine;
    }

    private static IReadOnlyList<string> ReadChunks(JsonElement root, long size, int chunkSize)
    {
        JsonElement element = Require(root, "chunks", JsonValueKind.Array, "an array");
        long expected = (size + chunkSize - 1) / chunkSize;
        if (expected > MaxChunks)
        {
            throw Invalid($"'size' and 'chunkSize' give {expected} chunks, more than the {MaxChunks} accepted");
        }

        var hashes = new List<string>((int)expected);
        foreach (JsonElement chunk in element.EnumerateArray())
        {
            if (chunk.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"'chunks[{hashes.Count}]' must be an object");
            }

            string? hash = Require(chunk, "sha256", JsonValueKind.String, "a string").GetString();
            if (!IsLowercaseSha256(hash))
            {
                throw Invalid($"'chunks[{hashes.Count}].sha256' must be 64 lowercase hex digits");
            }

            hashes.Add(hash!);
        }

        if (hashes.Count != expected)
        {
            throw Invalid($"'chunks' has {hashes.Count} entries; 'size' and 'chunkSize' give {expected}");
        }

        return hashes;
    }

    private static IReadOnlyDictionary<string, ImageFileEntry> ReadFiles(JsonElement root, long size)
    {
        JsonElement element = Require(root, "files", JsonValueKind.Object, "an object");
        var files = new Dictionary<string, ImageFileEntry>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!StorageLayout.IsValidFileName(property.Name))
            {
                throw Invalid($"'files' has the name '{property.Name}', which is not a file name (lowercase letters, digits, dots, dashes, underscores)");
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"'files.{property.Name}' must be an object");
            }

            long offset = ReadInteger(property.Value, "offset", 0, size);
            long length = ReadInteger(property.Value, "size", 0, size);
            if (offset + length > size)
            {
                throw Invalid($"'files.{property.Name}' ends at {offset + length}, beyond the payload size {size}");
            }

            files[property.Name] = new ImageFileEntry(offset, length);
        }

        return files;
    }

    /// <summary>Whether <paramref name="value"/> is a SHA-256 in lowercase hex.</summary>
    internal static bool IsLowercaseSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
