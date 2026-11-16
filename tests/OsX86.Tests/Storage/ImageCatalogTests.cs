using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>The configuration of the known images (work item 6b): read, checked, resolved to the origin the store downloads from.</summary>
[Trait("Category", "unit")]
public sealed class ImageCatalogTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly Uri Page = new("http://localhost:5000/");

    private static string Catalog(string entries, int format = 1) => $$"""{"format": {{format}}, "images": [{{entries}}]}""";

    private static string Entry(string image = "linux-base", string version = "0.1.0", string url = "images/build/linux-base/pack/manifest.json", string? sha = null) =>
        $$"""{"image": "{{image}}", "version": "{{version}}", "manifestUrl": "{{url}}", "manifestSha256": "{{sha ?? Hash}}"}""";

    [Fact]
    public void A_catalog_lists_its_images_and_resolves_a_relative_address_against_the_page()
    {
        ImageCatalog catalog = ImageCatalog.Parse(Catalog(Entry() + "," + Entry("freedos", "1.4.0", "https://images.example/freedos/1.4.0/manifest.json", new string('B', 64))) + " ", Page);

        Assert.Equal(2, catalog.Images.Count);
        Assert.Equal(Page, catalog.BaseAddress);
        ImageSource local = catalog.SourceOf("linux-base", "0.1.0")!;
        Assert.Equal("http://localhost:5000/images/build/linux-base/pack/", local.BaseUrl);
        Assert.Equal(Hash, local.ManifestSha256);
        Assert.Equal("http://localhost:5000/images/build/linux-base/pack/manifest.json", local.ManifestUrl);
        ImageSource remote = catalog.SourceOf("freedos", "1.4.0")!;
        Assert.Equal("https://images.example/freedos/1.4.0/", remote.BaseUrl);
        Assert.Equal(new string('b', 64), remote.ManifestSha256);
        Assert.Null(catalog.SourceOf("linux-base", "0.2.0"));
        Assert.Null(catalog.Find("wine", "0.1.0"));
    }

    [Fact]
    public void Comments_trailing_commas_and_unknown_properties_are_accepted()
    {
        ImageCatalog catalog = ImageCatalog.Parse("""
            // the images of this host
            {"format": 1, "origin": "local", "images": [{"image": "linux-base", "version": "0.1.0", "manifestUrl": "manifest.json", "manifestSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "note": "x"},]}
            """, new Uri("https://host.example/dir/"));

        Assert.Equal("https://host.example/dir/", catalog.SourceOf("linux-base", "0.1.0")!.BaseUrl);
        Assert.Empty(ImageCatalog.Parse("""{"format": 1}""").Images);
        Assert.Empty(ImageCatalog.Empty.Images);
    }

    [Theory]
    [InlineData("{\"format\": 1, \"images\": [null]}", "null")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("null", "empty")]
    public void A_malformed_catalog_is_refused(string json, string expected)
    {
        StorageException error = Assert.Throws<StorageException>(() => ImageCatalog.Parse(json));
        Assert.Equal(StorageErrorCodes.ManifestInvalid, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_format_is_refused()
    {
        StorageException error = Assert.Throws<StorageException>(() => ImageCatalog.Parse(Catalog(Entry(), format: 2)));
        Assert.Equal(StorageErrorCodes.FormatUnsupported, error.Code);
    }

    [Fact]
    public void A_catalog_larger_than_the_limit_is_refused()
    {
        string huge = Catalog(string.Join(",", Enumerable.Repeat(Entry(), 2000)));
        Assert.True(huge.Length > ImageCatalog.MaxBytes);
        Assert.Contains("bytes accepted", Assert.Throws<StorageException>(() => ImageCatalog.Parse(huge)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Linux Base", "0.1.0", "a/manifest.json", null, "not an image identifier")]
    [InlineData("linux-base", "latest", "a/manifest.json", null, "not a semantic version")]
    [InlineData("linux-base", "0.1.0", "a/image.bin", null, "must end with /manifest.json")]
    [InlineData("linux-base", "0.1.0", "", null, "must end with /manifest.json")]
    [InlineData("linux-base", "0.1.0", "a/manifest.json", "abc", "64 hex digits")]
    public void An_invalid_entry_is_refused_naming_the_problem(string image, string version, string url, string? sha, string expected)
    {
        StorageException error = Assert.Throws<StorageException>(() => ImageCatalog.Parse(Catalog(Entry(image, version, url, sha))));
        Assert.Equal(StorageErrorCodes.ManifestInvalid, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_version_listed_twice_is_refused()
    {
        StorageException error = Assert.Throws<StorageException>(() => ImageCatalog.Parse(Catalog(Entry() + "," + Entry())));
        Assert.Contains("listed twice", error.Message, StringComparison.Ordinal);
        Assert.Throws<StorageException>(() => new ImageCatalog([null!]));
        Assert.Throws<ArgumentException>(() => new ImageCatalog([], new Uri("relative/", UriKind.Relative)));
    }

    [Fact]
    public void An_origin_the_store_would_refuse_is_refused_when_resolved()
    {
        var plain = new KnownImage("linux-base", "0.1.0", "http://images.example/linux-base/manifest.json", Hash);
        Assert.Contains("https", Assert.Throws<StorageException>(() => plain.ToSource(null)).Message, StringComparison.Ordinal);

        var relative = new KnownImage("linux-base", "0.1.0", "linux-base/manifest.json", Hash);
        Assert.Contains("no base address", Assert.Throws<StorageException>(() => relative.ToSource(null)).Message, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:8080/linux-base/", relative.ToSource(new Uri("http://127.0.0.1:8080/")).BaseUrl);

        var query = new KnownImage("linux-base", "0.1.0", "https://images.example/linux-base/manifest.json?token=1", Hash);
        Assert.Throws<StorageException>(() => query.Validate()); // the address must end with the manifest file
    }
}
