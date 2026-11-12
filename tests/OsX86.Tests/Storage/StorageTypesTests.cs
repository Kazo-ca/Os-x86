using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>The small types of the storage layer: layout and names, the download state machine, the records.</summary>
[Trait("Category", "unit")]
public sealed class StorageTypesTests
{
    [Theory]
    [InlineData("alpine-test", true)]
    [InlineData("a", true)]
    [InlineData("linux-base-2", true)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("Alpine", false)]
    [InlineData("a.b", false)]
    [InlineData("a/b", false)]
    [InlineData("alpine\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Image_identifiers_are_lowercase_dashed_names(string? image, bool valid)
    {
        Assert.Equal(valid, StorageLayout.IsValidImageId(image));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("0.1.2-beta.1+build.5", true)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0", false)]
    [InlineData("..", false)]
    [InlineData("1.0.0/x", false)]
    [InlineData("1.0.0\n", false)]
    [InlineData(null, false)]
    public void Versions_are_semantic_versions(string? version, bool valid)
    {
        Assert.Equal(valid, StorageLayout.IsValidVersion(version));
    }

    [Theory]
    [InlineData("kernel", true)]
    [InlineData("rootfs.9p", true)]
    [InlineData("boot_disk-1", true)]
    [InlineData(".hidden", false)]
    [InlineData("Kernel", false)]
    [InlineData("a/b", false)]
    [InlineData("kernel\n", false)]
    public void File_names_are_plain(string name, bool valid)
    {
        Assert.Equal(valid, StorageLayout.IsValidFileName(name));
    }

    [Fact]
    public void Names_longer_than_the_limit_are_refused()
    {
        Assert.False(StorageLayout.IsValidImageId(new string('a', StorageLayout.MaxNameLength + 1)));
        Assert.True(StorageLayout.IsValidImageId(new string('a', StorageLayout.MaxNameLength)));
        Assert.False(StorageLayout.IsValidVersion("1.0.0-" + new string('a', StorageLayout.MaxNameLength)));
        Assert.False(StorageLayout.IsValidFileName(new string('a', StorageLayout.MaxNameLength + 1)));
    }

    [Fact]
    public void Paths_are_under_the_images_directory()
    {
        Assert.Equal("osx86/images/alpine-test/1.0.0", StorageLayout.ImageDirectory("alpine-test", "1.0.0"));
        Assert.Equal("osx86/images/alpine-test/1.0.0/image.bin", StorageLayout.PayloadPath("alpine-test", "1.0.0"));
        Assert.Throws<ArgumentException>(() => StorageLayout.ImageDirectory("../etc", "1.0.0"));
        Assert.Throws<ArgumentException>(() => StorageLayout.ImageDirectory("alpine-test", ".."));
    }

    [Theory]
    [InlineData(DownloadPhase.Idle, DownloadPhase.FetchingManifest, true)]
    [InlineData(DownloadPhase.FetchingManifest, DownloadPhase.Planning, true)]
    [InlineData(DownloadPhase.Planning, DownloadPhase.Downloading, true)]
    [InlineData(DownloadPhase.Downloading, DownloadPhase.Completed, true)]
    [InlineData(DownloadPhase.Planning, DownloadPhase.Failed, true)]
    [InlineData(DownloadPhase.Downloading, DownloadPhase.Cancelled, true)]
    [InlineData(DownloadPhase.Idle, DownloadPhase.Downloading, false)]
    [InlineData(DownloadPhase.Planning, DownloadPhase.Completed, false)]
    [InlineData(DownloadPhase.Completed, DownloadPhase.Failed, false)]
    [InlineData(DownloadPhase.Failed, DownloadPhase.FetchingManifest, false)]
    [InlineData(DownloadPhase.Downloading, DownloadPhase.Idle, false)]
    public void The_download_state_machine_allows_the_lifecycle_only(DownloadPhase from, DownloadPhase to, bool allowed)
    {
        Assert.Equal(allowed, DownloadStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void Final_phases_and_writing_phases_are_named()
    {
        Assert.True(DownloadStateMachine.IsFinal(DownloadPhase.Completed));
        Assert.True(DownloadStateMachine.IsFinal(DownloadPhase.Cancelled));
        Assert.False(DownloadStateMachine.IsFinal(DownloadPhase.Downloading));
        Assert.True(DownloadStateMachine.MayHaveWritten(DownloadPhase.Downloading));
        Assert.False(DownloadStateMachine.MayHaveWritten(DownloadPhase.Planning));
        Assert.False(DownloadStateMachine.MayHaveWritten(DownloadPhase.FetchingManifest));
    }

    [Fact]
    public void Progress_gives_a_fraction_between_zero_and_one()
    {
        Assert.Equal(0.25, new DownloadProgress(DownloadPhase.Downloading, 25, 100, 1, 4, 10).Fraction);
        Assert.Equal(0, new DownloadProgress(DownloadPhase.FetchingManifest, 0, 0, 0, 0, 0).Fraction);
        Assert.Equal(1, new DownloadProgress(DownloadPhase.Completed, 200, 100, 4, 4, 0).Fraction);
    }

    [Theory]
    [InlineData("https://images.example/alpine-test/1.0.0/", 2, true)]
    [InlineData("http://localhost:8080/image/", 1, true)]
    [InlineData("http://127.0.0.1:8080/image/", 1, true)]
    [InlineData("http://images.example/a/", 2, false)]
    [InlineData("https://images.example/alpine-test/1.0.0", 2, false)]
    [InlineData("ftp://images.example/a/", 2, false)]
    [InlineData("not a url", 2, false)]
    [InlineData("https://images.example/a/", 0, false)]
    [InlineData("https://images.example/a/", 5, false)]
    public void An_image_source_is_an_http_directory_with_a_bounded_parallelism(string baseUrl, int parallelism, bool valid)
    {
        var source = new ImageSource(baseUrl, new string('a', 64), parallelism);
        if (valid)
        {
            source.Validate();
            Assert.Equal(baseUrl + "manifest.json", source.ManifestUrl);
        }
        else
        {
            Assert.Throws<ArgumentException>(source.Validate);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("zz")]
    public void An_image_source_needs_a_hex_hash(string hash)
    {
        Assert.Throws<ArgumentException>(() => new ImageSource("https://images.example/a/", hash).Validate());
    }

    [Fact]
    public void A_stored_image_knows_its_paths_and_support()
    {
        var supported = new StoredImage("alpine-test", "1.0.0", 100, 120, true, 1, new Dictionary<string, string>());
        var unknown = supported with { Format = 2 };

        Assert.True(supported.IsSupported);
        Assert.False(unknown.IsSupported);
        Assert.Equal("osx86/images/alpine-test/1.0.0", supported.Directory);
        Assert.Equal("osx86/images/alpine-test/1.0.0/image.bin", supported.PayloadPath);
    }

    [Fact]
    public void A_verification_is_sound_without_bad_chunks()
    {
        Assert.True(new VerificationResult(4, []).IsSound);
        Assert.False(new VerificationResult(4, [2]).IsSound);
    }

    [Fact]
    public void A_storage_exception_needs_a_code()
    {
        var exception = new StorageException(StorageErrorCodes.Network, "cut", new TimeoutException());
        Assert.Equal("network", exception.Code);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Throws<ArgumentException>(() => new StorageException("", "x"));
    }
}
