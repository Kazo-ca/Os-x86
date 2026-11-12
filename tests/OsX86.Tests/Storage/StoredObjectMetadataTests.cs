using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary><see cref="StoredObjectMetadata"/>: the versioned sidecar of plan section 13.</summary>
[Trait("Category", "unit")]
public sealed class StoredObjectMetadataTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_metadata_of_an_image_names_its_format_image_and_engine()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.Valid());

        StoredObjectMetadata metadata = StoredObjectMetadata.ForImage(manifest, Created);
        string json = metadata.ToJson();

        Assert.Equal(StoredObjectMetadata.CurrentFormat, metadata.Format);
        Assert.Equal(StoredObjectKind.Image, metadata.Kind);
        Assert.False(metadata.Complete);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("format").GetInt32());
        Assert.Equal("image", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal("alpine-test", document.RootElement.GetProperty("image").GetString());
        Assert.Equal("1.0.0", document.RootElement.GetProperty("imageVersion").GetString());
        Assert.Equal("0.5.473", document.RootElement.GetProperty("engine").GetProperty("v86").GetString());
        Assert.Equal("2026-11-12T10:00:00+00:00", document.RootElement.GetProperty("created").GetString());
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void The_metadata_round_trips_and_the_worker_may_mark_it_complete()
    {
        string json = StoredObjectMetadata.ForImage(ImageManifest.Parse(TestManifests.Valid()), Created).ToJson();
        string marked = json.Replace("\"complete\":false", "\"complete\":true", StringComparison.Ordinal);

        StoredObjectMetadata metadata = StoredObjectMetadata.Parse(marked);

        Assert.True(metadata.Complete);
        Assert.Equal(Created, metadata.Created);
        Assert.Equal("alpine-test", metadata.Image);
        Assert.Equal("1.0.0", metadata.ImageVersion);
        Assert.Equal("0.5.473", metadata.Engine["v86"]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void An_unknown_format_is_refused_and_says_so(int format)
    {
        string json = StoredObjectMetadata.ForImage(ImageManifest.Parse(TestManifests.Valid()), Created).ToJson()
            .Replace("\"format\":1", $"\"format\":{format}", StringComparison.Ordinal);

        var error = Assert.Throws<StorageException>(() => StoredObjectMetadata.Parse(json));

        Assert.Equal(StorageErrorCodes.FormatUnsupported, error.Code);
        Assert.Contains($"format {format}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"image\"}")]
    [InlineData("{\"format\":\"1\"}")]
    [InlineData("{\"format\":1,\"kind\":\"image\",\"image\":\"Bad\",\"imageVersion\":\"1.0.0\",\"engine\":{\"v86\":\"1\"},\"created\":\"2026-11-12T10:00:00Z\"}")]
    [InlineData("{\"format\":1,\"kind\":\"image\",\"image\":\"ok\",\"imageVersion\":\"1.0.0\",\"engine\":{},\"created\":\"2026-11-12T10:00:00Z\"}")]
    [InlineData("{\"format\":1,\"kind\":\"image\",\"image\":\"ok\",\"imageVersion\":\"1.0.0\",\"engine\":null,\"created\":\"2026-11-12T10:00:00Z\"}")]
    [InlineData("{\"format\":1,\"kind\":\"image\",\"image\":\"ok\",\"imageVersion\":\"1.0.0\",\"engine\":{\"v86\":\"1\"},\"created\":\"not a date\"}")]
    public void An_invalid_metadata_is_refused(string json)
    {
        Assert.Equal(StorageErrorCodes.ManifestInvalid, Assert.Throws<StorageException>(() => StoredObjectMetadata.Parse(json)).Code);
    }

    [Fact]
    public void An_oversized_metadata_is_refused()
    {
        Assert.Equal(StorageErrorCodes.ManifestInvalid, Assert.Throws<StorageException>(() => StoredObjectMetadata.Parse(new string(' ', StoredObjectMetadata.MaxBytes + 1))).Code);
    }

    [Fact]
    public void Every_kind_of_stored_object_serializes_by_name()
    {
        var metadata = new StoredObjectMetadata
        {
            Kind = StoredObjectKind.RestorePoint,
            Image = "linux-base",
            ImageVersion = "2.0.0",
            Engine = new Dictionary<string, string> { ["v86"] = "0.5.473" },
            Created = Created,
            Complete = true,
        };

        string json = metadata.ToJson();

        Assert.Contains("\"kind\":\"restore-point\"", json, StringComparison.Ordinal);
        Assert.Equal(StoredObjectKind.RestorePoint, StoredObjectMetadata.Parse(json).Kind);
    }
}
