using System.Text;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary><see cref="ImageManifest"/>: the schema shared with work item 4, its validation, unknown formats, hashes.</summary>
[Trait("Category", "unit")]
public sealed class ImageManifestTests
{
    [Fact]
    public void A_valid_manifest_is_read_whole()
    {
        string json = TestManifests.Valid();

        ImageManifest manifest = ImageManifest.Parse(json);

        Assert.Equal(1, manifest.Format);
        Assert.Equal("alpine-test", manifest.Image);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("0.5.473", manifest.V86Version);
        Assert.Equal(TestManifests.Size, manifest.Size);
        Assert.Equal(ImageManifest.DefaultChunkSize, manifest.ChunkSize);
        Assert.Equal(4, manifest.ChunkCount);
        Assert.Equal(TestManifests.FakeHash(3), manifest.ChunkHashes[3]);
        Assert.Equal(new ImageFileEntry(4096, TestManifests.Size - 4096), manifest.Files["initramfs"]);
        Assert.Same(json, manifest.Json);
        Assert.Equal(SimulatedStorageWorkerChannel.Sha256Of(json), manifest.Sha256);
    }

    [Fact]
    public void Chunk_names_and_lengths_follow_the_schema()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.Valid());

        Assert.Equal("chunk-00000.bin", ImageManifest.ChunkFileName(0));
        Assert.Equal("chunk-00007.bin", ImageManifest.ChunkFileName(7));
        Assert.Equal(ImageManifest.DefaultChunkSize, manifest.ChunkLength(0));
        Assert.Equal(TestManifests.Size - 3L * ImageManifest.DefaultChunkSize, manifest.ChunkLength(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => manifest.ChunkLength(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageManifest.ChunkFileName(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageManifest.ChunkFileName(ImageManifest.MaxChunks));
    }

    [Fact]
    public void A_one_chunk_image_has_one_short_chunk()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.Valid(size: 10, chunkSize: ImageManifest.MinChunkSize));
        Assert.Equal(1, manifest.ChunkCount);
        Assert.Equal(10, manifest.ChunkLength(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void An_unknown_format_is_refused_and_says_so(int format)
    {
        var error = Assert.Throws<StorageException>(() => ImageManifest.Parse(TestManifests.With("format", format)));

        Assert.Equal(StorageErrorCodes.FormatUnsupported, error.Code);
        Assert.Contains($"format {format}", error.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was written", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_expected_hash_accepts_the_manifest_in_any_hex_case()
    {
        string json = TestManifests.Valid();
        string hash = SimulatedStorageWorkerChannel.Sha256Of(json);

        ImageManifest lower = ImageManifest.Parse(Encoding.UTF8.GetBytes(json), hash);
        ImageManifest upper = ImageManifest.Parse(Encoding.UTF8.GetBytes(json), hash.ToUpperInvariant());

        Assert.Equal(hash, lower.Sha256);
        Assert.Equal(hash, upper.Sha256);
    }

    [Fact]
    public void A_different_hash_refuses_the_manifest_before_reading_it()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(TestManifests.With("format", 99));

        var error = Assert.Throws<StorageException>(() => ImageManifest.Parse(bytes, new string('0', 64)));

        Assert.Equal(StorageErrorCodes.ManifestIntegrity, error.Code);
        Assert.Contains("refused", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_manifest_is_refused_before_hashing()
    {
        byte[] bytes = new byte[ImageManifest.MaxManifestBytes + 1];
        Assert.Equal(StorageErrorCodes.ManifestInvalid, Assert.Throws<StorageException>(() => ImageManifest.Parse(bytes, new string('0', 64))).Code);
        Assert.Equal(StorageErrorCodes.ManifestInvalid, Assert.Throws<StorageException>(() => ImageManifest.Parse(new string(' ', ImageManifest.MaxManifestBytes + 1))).Code);
    }

    public static TheoryData<string, string> InvalidManifests => new()
    {
        { "not json", "{" },
        { "not an object", "[1]" },
        { "format missing", TestManifests.With("format", TestManifests.Removed) },
        { "format not a number", TestManifests.With("format", "1") },
        { "format not an integer", TestManifests.With("format", 1.5) },
        { "image missing", TestManifests.With("image", TestManifests.Removed) },
        { "image with slash", TestManifests.With("image", "../x") },
        { "image uppercase", TestManifests.With("image", "Alpine") },
        { "image too long", TestManifests.With("image", new string('a', 65)) },
        { "version not semver", TestManifests.With("version", "v1") },
        { "version with slash", TestManifests.With("version", "1.0.0/x") },
        { "engine missing", TestManifests.With("engine", TestManifests.Removed) },
        { "engine without v86", TestManifests.With("engine", new Dictionary<string, object?> { ["qemu"] = "1" }) },
        { "engine value empty", TestManifests.With("engine", new Dictionary<string, object?> { ["v86"] = "" }) },
        { "engine value not a string", TestManifests.With("engine", new Dictionary<string, object?> { ["v86"] = 5 }) },
        { "size zero", TestManifests.With("size", 0) },
        { "size negative", TestManifests.With("size", -1) },
        { "size not an integer", TestManifests.With("size", 1.5) },
        { "chunkSize too small", TestManifests.With("chunkSize", ImageManifest.MinChunkSize - 1) },
        { "chunkSize too large", TestManifests.With("chunkSize", (long)ImageManifest.MaxChunkSize + 1) },
        { "chunks missing", TestManifests.With("chunks", TestManifests.Removed) },
        { "chunks not an array", TestManifests.With("chunks", "x") },
        { "chunk not an object", TestManifests.With("chunks", new List<object?> { "x" }) },
        { "chunk without sha256", TestManifests.With("chunks", new List<object?> { new Dictionary<string, object?>() }) },
        { "chunk sha256 uppercase", TestManifests.With("chunks", Enumerable.Range(0, 4).Select(i => new Dictionary<string, object?> { ["sha256"] = TestManifests.FakeHash(i).ToUpperInvariant() }).ToList()) },
        { "chunk sha256 too short", TestManifests.With("chunks", Enumerable.Range(0, 4).Select(_ => new Dictionary<string, object?> { ["sha256"] = "abc" }).ToList()) },
        { "too few chunks", TestManifests.With("chunks", Enumerable.Range(0, 3).Select(i => new Dictionary<string, object?> { ["sha256"] = TestManifests.FakeHash(i) }).ToList()) },
        { "too many chunks", TestManifests.With("chunks", Enumerable.Range(0, 5).Select(i => new Dictionary<string, object?> { ["sha256"] = TestManifests.FakeHash(i) }).ToList()) },
        { "files missing", TestManifests.With("files", TestManifests.Removed) },
        { "file name invalid", TestManifests.With("files", new Dictionary<string, object?> { ["Kernel"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 1 } }) },
        { "file not an object", TestManifests.With("files", new Dictionary<string, object?> { ["kernel"] = 1 }) },
        { "file offset missing", TestManifests.With("files", new Dictionary<string, object?> { ["kernel"] = new Dictionary<string, object?> { ["size"] = 1 } }) },
        { "file beyond the payload", TestManifests.With("files", new Dictionary<string, object?> { ["kernel"] = new Dictionary<string, object?> { ["offset"] = TestManifests.Size - 1, ["size"] = 2 } }) },
        { "file offset negative", TestManifests.With("files", new Dictionary<string, object?> { ["kernel"] = new Dictionary<string, object?> { ["offset"] = -1, ["size"] = 2 } }) },
    };

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void An_invalid_manifest_is_refused_with_a_reason(string reason, string json)
    {
        var error = Assert.Throws<StorageException>(() => ImageManifest.Parse(json));

        Assert.Equal(StorageErrorCodes.ManifestInvalid, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message), reason);
    }

    [Fact]
    public void Too_many_chunks_for_the_file_names_are_refused()
    {
        string json = TestManifests.With("size", (long)ImageManifest.MinChunkSize * (ImageManifest.MaxChunks + 1));
        json = json.Replace($"\"chunkSize\":{ImageManifest.DefaultChunkSize}", $"\"chunkSize\":{ImageManifest.MinChunkSize}", StringComparison.Ordinal);

        var error = Assert.Throws<StorageException>(() => ImageManifest.Parse(json));

        Assert.Equal(StorageErrorCodes.ManifestInvalid, error.Code);
        Assert.Contains("more than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_fields_are_ignored_for_forward_compatibility()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.With("comment", "added by a later minor revision"));
        Assert.Equal("alpine-test", manifest.Image);
    }

    [Fact]
    public void An_empty_files_object_is_allowed()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.With("files", new Dictionary<string, object?>()));
        Assert.Empty(manifest.Files);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ImageManifest.Parse(null!));
        Assert.Throws<ArgumentException>(() => ImageManifest.Parse([], ""));
    }
}
