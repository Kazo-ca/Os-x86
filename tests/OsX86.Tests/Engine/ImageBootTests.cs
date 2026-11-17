using System.Text.Json;
using OsX86.Engine;
using OsX86.Engine.V86;
using OsX86.Storage;
using OsX86.Tests.Images;
using OsX86.Tests.Storage;

namespace OsX86.Tests.Engine;

/// <summary>
/// Work item 6a, C# side: the start options with disks and layers, the start command they become,
/// <see cref="ImageBoot"/> (the boot section of a manifest turned into options, the rule of the same
/// major version of decision 169), and the queries of <see cref="V86Engine"/>.
/// </summary>
[Trait("Category", "unit")]
public sealed class ImageBootTests
{
    private const long Size = TestManifests.Size;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Created = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A linux-base like manifest: kernel, initramfs and rootfs compressed, rootfs on hda.</summary>
    private static Dictionary<string, object?> LinuxManifest(string v86 = "0.5.473", object? boot = null)
    {
        Dictionary<string, object?> manifest = TestManifests.ValidObject();
        manifest["image"] = "linux-base";
        manifest["engine"] = new Dictionary<string, object?> { ["v86"] = v86 };
        manifest["files"] = new Dictionary<string, object?>
        {
            ["kernel"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 1000, ["rawSize"] = 2000, ["blockSize"] = 1048576 },
            ["initramfs"] = new Dictionary<string, object?> { ["offset"] = 1000, ["size"] = 500, ["rawSize"] = 900, ["blockSize"] = 1048576 },
            ["rootfs"] = new Dictionary<string, object?> { ["offset"] = 1500, ["size"] = Size - 1500, ["rawSize"] = 130023424L, ["blockSize"] = 1048576 },
        };
        manifest["boot"] = boot ?? new Dictionary<string, object?>
        {
            ["kind"] = "linux",
            ["cmdline"] = "root=/dev/sda rootfstype=ext4 ro console=ttyS0",
            ["disks"] = new Dictionary<string, object?> { ["hda"] = "rootfs" },
            ["readyMarker"] = "OSX86-READY",
            ["prompt"] = "osx86# ",
            ["memoryMiB"] = 256,
            ["agent"] = new Dictionary<string, object?> { ["port"] = "virtio0" },
        };
        return manifest;
    }

    private static ImageManifest Parse(Dictionary<string, object?> manifest) => ImageManifest.Parse(TestManifests.Json(manifest));

    [Fact]
    public void A_linux_manifest_boots_its_kernel_and_initramfs_from_the_payload_and_its_rootfs_on_hda()
    {
        EngineStartOptions options = ImageBoot.CreateStartOptions(Parse(LinuxManifest()));

        Assert.Equal(EngineBootKind.Linux, options.Kind);
        Assert.Equal(EngineFileSource.Seekable("osx86/images/linux-base/1.0.0/image.bin", 0, 1000, 2000, 1048576), options.Kernel);
        Assert.Equal(EngineFileSource.Seekable("osx86/images/linux-base/1.0.0/image.bin", 1000, 500, 900, 1048576), options.Initrd);
        Assert.Null(options.KernelPath);
        EngineDiskOptions hda = Assert.Single(options.Disks).Value;
        Assert.Equal(130023424L, hda.Source.RawSize);
        Assert.Same(EngineWriteLayer.Discard, hda.Layer);
        Assert.Equal("root=/dev/sda rootfstype=ext4 ro console=ttyS0", options.KernelCommandLine);
        Assert.Equal(256, options.MemoryMiB);
        Assert.Equal(EngineStartOptions.DefaultFrameCacheMiB, options.FrameCacheMiB);
    }

    [Fact]
    public void A_persistent_start_puts_each_disk_on_a_layer_with_its_versioned_metadata()
    {
        EngineStartOptions options = ImageBoot.CreateStartOptions(Parse(LinuxManifest()), new ImageBootOptions
        {
            LayerMode = EngineWriteLayerMode.Persistent,
            LayerSet = "vm-1",
            Created = Created,
            MemoryMiB = 512,
            KernelCommandLine = "console=ttyS0 single",
            FrameCacheMiB = 64,
        });

        EngineWriteLayer layer = options.Disks["hda"].Layer;
        Assert.Equal(EngineWriteLayerMode.Persistent, layer.Mode);
        Assert.Equal("osx86/layers/vm-1/hda.layer", layer.Path);
        StoredObjectMetadata metadata = StoredObjectMetadata.Parse(layer.MetadataJson!);
        Assert.Equal(StoredObjectKind.Layer, metadata.Kind);
        Assert.Equal("linux-base", metadata.Image);
        Assert.Equal("1.0.0", metadata.ImageVersion);
        Assert.Equal(V86Engine.V86Version, metadata.Engine["v86"]);
        Assert.Equal(Created, metadata.Created);
        Assert.Equal(512, options.MemoryMiB);
        Assert.Equal("console=ttyS0 single", options.KernelCommandLine);
        Assert.Equal(64, options.FrameCacheMiB);
        Assert.Throws<ArgumentException>(() => ImageBoot.CreateStartOptions(Parse(LinuxManifest()), new ImageBootOptions { LayerMode = EngineWriteLayerMode.Persistent }));
    }

    [Fact]
    public void A_dos_manifest_boots_its_disk_without_kernel()
    {
        Dictionary<string, object?> manifest = TestManifests.ValidObject();
        manifest["image"] = "freedos";
        manifest["files"] = new Dictionary<string, object?> { ["disk"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = Size, ["rawSize"] = 33554432, ["blockSize"] = 1048576 } };
        manifest["boot"] = new Dictionary<string, object?> { ["kind"] = "dos", ["disks"] = new Dictionary<string, object?> { ["hda"] = "disk" }, ["display"] = "vga-text", ["readyMarker"] = "OSX86-READY", ["memoryMiB"] = 64 };

        EngineStartOptions options = ImageBoot.CreateStartOptions(Parse(manifest));

        Assert.Equal(EngineBootKind.Dos, options.Kind);
        Assert.Null(options.Kernel);
        Assert.Null(options.EffectiveInitrd);
        Assert.Equal(33554432, options.Disks["hda"].Source.RawSize);
        Assert.Equal(64, options.MemoryMiB);
        using JsonDocument command = JsonDocument.Parse(WorkerProtocol.Start(options));
        JsonElement sent = command.RootElement.GetProperty("options");
        Assert.Equal("dos", sent.GetProperty("kind").GetString());
        Assert.False(sent.TryGetProperty("kernel", out _));
        Assert.Equal("discard", sent.GetProperty("disks").GetProperty("hda").GetProperty("layer").GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("1.0.0")]
    [InlineData("0.4.99")]
    public void An_image_of_another_major_version_of_v86_is_refused(string built)
    {
        var error = Assert.Throws<EngineException>(() => ImageBoot.CreateStartOptions(Parse(LinuxManifest(built))));
        Assert.Equal(EngineErrorCodes.EngineVersionMismatch, error.Code);
        Assert.Contains(built, error.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was read or written", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_of_the_same_major_version_starts()
    {
        Assert.Equal(EngineBootKind.Linux, ImageBoot.CreateStartOptions(Parse(LinuxManifest("0.5.999"))).Kind);
        ImageBoot.EnsureSameMajorVersion("2.0.0", "2.7.1", "a layer");
        Assert.Throws<EngineException>(() => ImageBoot.EnsureSameMajorVersion("2.0.0", "3.0.0", "a layer"));
    }

    [Theory]
    [InlineData("0.5.473", "0.5")]
    [InlineData("1.2.3", "1")]
    [InlineData("10.0.0-rc.1", "10")]
    [InlineData("nightly", "nightly")]
    public void The_major_version_follows_semver(string version, string major) => Assert.Equal(major, ImageBoot.MajorVersion(version));

    [Fact]
    public void A_manifest_the_engine_cannot_follow_is_refused_with_boot_unsupported()
    {
        Dictionary<string, object?> withoutBoot = LinuxManifest();
        withoutBoot.Remove("boot");
        Assert.Equal(EngineErrorCodes.BootUnsupported, Assert.Throws<EngineException>(() => ImageBoot.CreateStartOptions(Parse(withoutBoot))).Code);

        var unknownKind = new Dictionary<string, object?> { ["kind"] = "windows" };
        Assert.Equal(EngineErrorCodes.BootUnsupported, Assert.Throws<EngineException>(() => ImageBoot.CreateStartOptions(Parse(LinuxManifest(boot: unknownKind)))).Code);

        var unknownDisk = new Dictionary<string, object?> { ["kind"] = "linux", ["disks"] = new Dictionary<string, object?> { ["sdz"] = "rootfs" } };
        Assert.Equal(EngineErrorCodes.BootUnsupported, Assert.Throws<EngineException>(() => ImageBoot.CreateStartOptions(Parse(LinuxManifest(boot: unknownDisk)))).Code);

        Dictionary<string, object?> withoutKernel = LinuxManifest();
        ((Dictionary<string, object?>)withoutKernel["files"]!).Remove("kernel");
        Assert.Equal(EngineErrorCodes.BootUnsupported, Assert.Throws<EngineException>(() => ImageBoot.CreateStartOptions(Parse(withoutKernel))).Code);
    }

    [Fact]
    public void An_uncompressed_file_becomes_a_plain_range()
    {
        Dictionary<string, object?> manifest = LinuxManifest();
        ((Dictionary<string, object?>)manifest["files"]!)["kernel"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 1000 };
        EngineStartOptions options = ImageBoot.CreateStartOptions(Parse(manifest));
        Assert.Equal(EngineFileSource.Range("osx86/images/linux-base/1.0.0/image.bin", 0, 1000), options.Kernel);
        Assert.False(options.Kernel!.IsCompressed);
    }

    [Fact]
    public void The_start_command_carries_the_sources_the_disks_and_their_layers()
    {
        EngineStartOptions options = ImageBoot.CreateStartOptions(Parse(LinuxManifest()), new ImageBootOptions { LayerMode = EngineWriteLayerMode.Persistent, LayerSet = "vm-1", Created = Created });

        using JsonDocument command = JsonDocument.Parse(WorkerProtocol.Start(options));
        JsonElement sent = command.RootElement.GetProperty("options");
        Assert.Equal("linux", sent.GetProperty("kind").GetString());
        Assert.False(sent.TryGetProperty("kernelPath", out _));
        JsonElement kernel = sent.GetProperty("kernel");
        Assert.Equal("osx86/images/linux-base/1.0.0/image.bin", kernel.GetProperty("path").GetString());
        Assert.Equal(2000, kernel.GetProperty("rawSize").GetInt64());
        Assert.Equal(1048576, kernel.GetProperty("blockSize").GetInt32());
        JsonElement hda = sent.GetProperty("disks").GetProperty("hda");
        Assert.Equal(1500, hda.GetProperty("source").GetProperty("offset").GetInt64());
        JsonElement layer = hda.GetProperty("layer");
        Assert.Equal("persistent", layer.GetProperty("mode").GetString());
        Assert.Equal("osx86/layers/vm-1/hda.layer", layer.GetProperty("path").GetString());
        Assert.Contains("\"kind\":\"layer\"", layer.GetProperty("metadata").GetString(), StringComparison.Ordinal);
        Assert.Equal(EngineStartOptions.DefaultFrameCacheMiB, sent.GetProperty("cacheMiB").GetInt32());
    }

    [Fact]
    public void Start_options_check_their_boot_files_disks_and_cache()
    {
        EngineFileSource kernel = EngineFileSource.Range("a/image.bin", 0, 10);
        EngineFileSource initrd = EngineFileSource.Range("a/image.bin", 10, 10);
        var disk = new EngineDiskOptions(EngineFileSource.Range("a/image.bin", 20, 10), EngineWriteLayer.Discard);
        new EngineStartOptions { Kernel = kernel, Initrd = initrd, Disks = new Dictionary<string, EngineDiskOptions> { ["hda"] = disk } }.Validate();
        new EngineStartOptions { Kind = EngineBootKind.Dos, Disks = new Dictionary<string, EngineDiskOptions> { ["hda"] = disk } }.Validate();

        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Initrd = initrd }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kernel = kernel }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kernel = kernel, KernelPath = "k", Initrd = initrd }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kernel = kernel, Initrd = initrd, InitrdPath = "i" }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kind = EngineBootKind.Dos }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kind = EngineBootKind.Dos, Kernel = kernel, Disks = new Dictionary<string, EngineDiskOptions> { ["hda"] = disk } }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kernel = kernel, Initrd = initrd, Disks = new Dictionary<string, EngineDiskOptions> { ["fda"] = disk } }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { Kernel = kernel, Initrd = initrd, Disks = new Dictionary<string, EngineDiskOptions> { ["hda"] = disk with { Layer = null! } } }.Validate());
        Assert.Throws<ArgumentNullException>(() => new EngineStartOptions { Kernel = kernel, Initrd = initrd, Disks = null! }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineStartOptions { Kernel = kernel, Initrd = initrd, FrameCacheMiB = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineStartOptions { Kernel = kernel, Initrd = initrd, FrameCacheMiB = 513 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineStartOptions { Kind = (EngineBootKind)9, Kernel = kernel, Initrd = initrd }.Validate());
    }

    [Fact]
    public void Sources_check_their_ranges_and_compression()
    {
        EngineFileSource.WholeFile("a/b").Validate("x");
        EngineFileSource.Seekable("a/b", 0, 10, 100, 4096).Validate("x");
        Assert.True(EngineFileSource.Seekable("a/b", 0, 10, 100, 4096).IsCompressed);
        Assert.Throws<ArgumentException>(() => new EngineFileSource("/abs").Validate("x"));
        Assert.Throws<ArgumentException>(() => new EngineFileSource("a", Offset: 1).Validate("x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineFileSource.Range("a", -1, 1).Validate("x"));
        Assert.Throws<ArgumentException>(() => new EngineFileSource("a", 0, 1, RawSize: 4).Validate("x"));
        Assert.Throws<ArgumentException>(() => new EngineFileSource("a", RawSize: 4, BlockSize: 4).Validate("x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineFileSource.Seekable("a", 0, 1, 4, 0).Validate("x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineFileSource.Seekable("a", 0, 1, -4, 4).Validate("x"));
        Assert.Throws<ArgumentException>(() => EngineWriteLayer.Persistent("../x", "{}"));
        Assert.Throws<ArgumentException>(() => EngineWriteLayer.Persistent("a/b.layer", " "));
        Assert.Equal(EngineWriteLayerMode.Discard, EngineWriteLayer.Discard.Mode);
        Assert.Null(EngineWriteLayer.Discard.Path);
    }

    [Fact]
    public void The_whole_file_paths_of_work_item_2_still_boot()
    {
        var options = new EngineStartOptions { KernelPath = "osx86-test/vmlinuz", InitrdPath = "osx86-test/initrd" };
        options.Validate();
        Assert.Equal(EngineFileSource.WholeFile("osx86-test/vmlinuz"), options.EffectiveKernel);
        Assert.Equal(EngineFileSource.WholeFile("osx86-test/initrd"), options.EffectiveInitrd);
        using JsonDocument command = JsonDocument.Parse(WorkerProtocol.Start(options));
        Assert.False(command.RootElement.GetProperty("options").TryGetProperty("disks", out _));
    }

    [Fact]
    public async Task The_engine_reads_the_text_screen_and_the_disk_counters_from_the_worker()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);

        EngineTextScreen screen = await engine.ReadTextScreenAsync(None);
        Assert.Equal(3, screen.Columns);
        Assert.Equal(2, screen.Rows);
        Assert.Equal("C:\\\nok", screen.Text);
        Assert.Equal(new byte[] { 7, 7, 7, 0x1E, 0x1E, 0x4F }, screen.Attributes);
        Assert.Equal(0x4F, screen.AttributeAt(1, 2));
        Assert.Equal(new EngineTextCursor(1, 2, true), screen.Cursor);
        Assert.True(screen.BlinkEnabled);
        Assert.False(screen.Graphical);

        EngineDiskStatistics statistics = await engine.GetDiskStatisticsAsync(None);
        EngineDiskCounters hda = statistics.Disks["hda"];
        Assert.Equal(new EngineDiskCounters(4096, 2, 1024, 1, 512, 300, EngineWriteLayerMode.Persistent, 1, 8192), hda);
        Assert.Equal(EngineWriteLayerMode.Discard, statistics.Disks["hdb"].LayerMode);
        Assert.Equal(new EngineFrameCacheCounters(1024, 2048, 1, 3, 1, 0), statistics.Cache);
        Assert.Equal(["read-screen", "query-disks"], worker.PostedTypes);
    }

    [Fact]
    public async Task A_query_answered_after_its_cancellation_does_not_reach_the_next_caller()
    {
        var worker = new SimulatedWorkerChannel { Silent = true };
        await using var engine = new V86Engine(worker);
        using var cancelled = new CancellationTokenSource();
        Task<EngineTextScreen> first = engine.ReadTextScreenAsync(cancelled.Token).AsTask();
        await WaitUntilAsync(() => worker.Posted.Count == 1);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Task<EngineTextScreen> second = engine.ReadTextScreenAsync(None).AsTask();
        await WaitUntilAsync(() => worker.Posted.Count == 2);
        await worker.SendAsync(new { type = "screen", cols = 1, rows = 1, lines = new[] { "a" } }); // the answer to the first
        await worker.SendAsync(new { type = "screen", cols = 1, rows = 1, lines = new[] { "b" } });
        Assert.Equal("b", (await second.WaitAsync(TimeSpan.FromSeconds(10))).Text);
        await worker.SendAsync(new { type = "disks" }); // nobody asked: dropped
    }

    [Fact]
    public async Task Pending_queries_fail_when_the_engine_is_disposed_or_cannot_post()
    {
        var silent = new SimulatedWorkerChannel { Silent = true };
        var engine = new V86Engine(silent);
        Task<EngineDiskStatistics> pending = engine.GetDiskStatisticsAsync(None).AsTask();
        await WaitUntilAsync(() => silent.Posted.Count == 1);
        await engine.DisposeAsync();
        Assert.Equal(EngineErrorCodes.Disposed, (await Assert.ThrowsAsync<EngineException>(() => pending)).Code);

        var broken = new SimulatedWorkerChannel();
        await using var other = new V86Engine(broken);
        await other.ReadTextScreenAsync(None);
        broken.PostException = new InvalidOperationException("gone");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await other.ReadTextScreenAsync(None));
        broken.PostException = null;
        Assert.Equal(3, (await other.ReadTextScreenAsync(None)).Columns); // the failed query left nothing in the queue
    }

    [Fact]
    public void The_engine_version_is_the_vendored_one()
    {
        string vendor = Path.Combine(PythonTools.RepositoryRoot, "src", "OsX86.Feature", "wwwroot", "v86", "VENDOR.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(vendor));
        Assert.Equal(V86Engine.V86Version, document.RootElement.GetProperty("version").GetString());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition was not met in 5 s");
    }
}
