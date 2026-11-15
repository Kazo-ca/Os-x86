using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>Work item 6a, storage side: the <c>boot</c> section and the compression fields of a manifest, the paths and metadata of write layers.</summary>
[Trait("Category", "unit")]
public sealed class ImageBootSectionTests
{
    private static Dictionary<string, object?> WithBoot(object? boot)
    {
        Dictionary<string, object?> manifest = TestManifests.ValidObject();
        manifest["boot"] = boot;
        return manifest;
    }

    [Fact]
    public void The_boot_section_is_read_with_its_disks_and_unknown_keys_are_ignored()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.Json(WithBoot(new Dictionary<string, object?>
        {
            ["kind"] = "linux",
            ["cmdline"] = "console=ttyS0",
            ["disks"] = new Dictionary<string, object?> { ["hda"] = "initramfs" },
            ["readyMarker"] = "OSX86-READY",
            ["prompt"] = "osx86# ",
            ["memoryMiB"] = 256,
            ["console"] = "serial0",
            ["agent"] = new Dictionary<string, object?> { ["port"] = "virtio0" },
        })));

        ImageBootSection boot = manifest.Boot!;
        Assert.Equal("linux", boot.Kind);
        Assert.Equal("console=ttyS0", boot.KernelCommandLine);
        Assert.Equal("initramfs", boot.Disks["hda"]);
        Assert.Equal("OSX86-READY", boot.ReadyMarker);
        Assert.Equal("osx86# ", boot.Prompt);
        Assert.Equal(256, boot.MemoryMiB);
        Assert.Equal("serial0", boot.Console);
    }

    [Fact]
    public void A_manifest_without_boot_has_none_and_a_minimal_one_has_defaults()
    {
        Assert.Null(ImageManifest.Parse(TestManifests.Valid()).Boot);
        ImageBootSection boot = ImageManifest.Parse(TestManifests.Json(WithBoot(new Dictionary<string, object?> { ["kind"] = "dos" }))).Boot!;
        Assert.Empty(boot.Disks);
        Assert.Null(boot.MemoryMiB);
        Assert.Null(boot.KernelCommandLine);
    }

    public static TheoryData<object?> InvalidBoots => new()
    {
        "linux",
        new Dictionary<string, object?>(),
        new Dictionary<string, object?> { ["kind"] = "Linux" },
        new Dictionary<string, object?> { ["kind"] = 3 },
        new Dictionary<string, object?> { ["kind"] = "linux", ["cmdline"] = "a\nb" },
        new Dictionary<string, object?> { ["kind"] = "linux", ["cmdline"] = "" },
        new Dictionary<string, object?> { ["kind"] = "linux", ["memoryMiB"] = 0 },
        new Dictionary<string, object?> { ["kind"] = "linux", ["memoryMiB"] = "256" },
        new Dictionary<string, object?> { ["kind"] = "linux", ["disks"] = "hda" },
        new Dictionary<string, object?> { ["kind"] = "linux", ["disks"] = new Dictionary<string, object?> { ["hda"] = "missing" } },
        new Dictionary<string, object?> { ["kind"] = "linux", ["disks"] = new Dictionary<string, object?> { ["HDA"] = "kernel" } },
        new Dictionary<string, object?> { ["kind"] = "linux", ["disks"] = new Dictionary<string, object?> { ["hda"] = 1 } },
    };

    [Theory]
    [MemberData(nameof(InvalidBoots))]
    public void An_invalid_boot_section_makes_the_manifest_invalid(object? boot)
    {
        var error = Assert.Throws<StorageException>(() => ImageManifest.Parse(TestManifests.Json(WithBoot(boot))));
        Assert.Equal(StorageErrorCodes.ManifestInvalid, error.Code);
    }

    [Fact]
    public void A_duplicate_disk_makes_the_manifest_invalid()
    {
        string json = TestManifests.Valid().TrimEnd('}') + ",\"boot\":{\"kind\":\"dos\",\"disks\":{\"hda\":\"kernel\",\"hda\":\"kernel\"}}}";
        Assert.Equal(StorageErrorCodes.ManifestInvalid, Assert.Throws<StorageException>(() => ImageManifest.Parse(json)).Code);
    }

    [Fact]
    public void The_compression_fields_of_a_file_are_read_both_or_neither()
    {
        Dictionary<string, object?> manifest = TestManifests.ValidObject();
        manifest["files"] = new Dictionary<string, object?>
        {
            ["rootfs"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 1000, ["rawSize"] = 130023424L, ["blockSize"] = 1048576 },
            ["plain"] = new Dictionary<string, object?> { ["offset"] = 1000, ["size"] = 10 },
        };
        ImageManifest parsed = ImageManifest.Parse(TestManifests.Json(manifest));
        Assert.Equal(new ImageFileEntry(0, 1000, 130023424L, 1048576), parsed.Files["rootfs"]);
        Assert.True(parsed.Files["rootfs"].IsCompressed);
        Assert.False(parsed.Files["plain"].IsCompressed);

        foreach (Dictionary<string, object?> bad in new[]
        {
            new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 10, ["rawSize"] = 100 },
            new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 10, ["blockSize"] = 100 },
            new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 10, ["rawSize"] = -1, ["blockSize"] = 100 },
            new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 10, ["rawSize"] = 100, ["blockSize"] = 0 },
            new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 10, ["rawSize"] = 100, ["blockSize"] = (1 << 30) + 1 },
        })
        {
            manifest["files"] = new Dictionary<string, object?> { ["rootfs"] = bad };
            Assert.Equal(StorageErrorCodes.ManifestInvalid, Assert.Throws<StorageException>(() => ImageManifest.Parse(TestManifests.Json(manifest))).Code);
        }
    }

    [Fact]
    public void Layer_paths_live_under_osx86_layers()
    {
        Assert.Equal("osx86/layers/vm-1", StorageLayout.LayerDirectory("vm-1"));
        Assert.Equal("osx86/layers/vm-1/hda.layer", StorageLayout.LayerPath("vm-1", "hda"));
        Assert.Throws<ArgumentException>(() => StorageLayout.LayerDirectory("../x"));
        Assert.Throws<ArgumentException>(() => StorageLayout.LayerPath("vm-1", "HDA"));
    }

    [Fact]
    public void The_metadata_of_a_layer_names_its_image_and_engine_and_parses_back()
    {
        ImageManifest manifest = ImageManifest.Parse(TestManifests.Valid());
        var created = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        string json = StoredObjectMetadata.ForLayer(manifest, "0.5.473", created).ToJson();
        StoredObjectMetadata parsed = StoredObjectMetadata.Parse(json);
        Assert.Equal(StoredObjectKind.Layer, parsed.Kind);
        Assert.Equal(manifest.Image, parsed.Image);
        Assert.Equal(manifest.Version, parsed.ImageVersion);
        Assert.Equal("0.5.473", parsed.Engine["v86"]);
        Assert.True(parsed.Complete);
        Assert.Contains("\"kind\":\"layer\"", json, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => StoredObjectMetadata.ForLayer(null!, "0.5.473", created));
        Assert.Throws<ArgumentException>(() => StoredObjectMetadata.ForLayer(manifest, "", created));
    }
}
