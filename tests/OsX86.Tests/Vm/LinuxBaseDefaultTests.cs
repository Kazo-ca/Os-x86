using Microsoft.JSInterop;
using OsX86.Engine;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Storage;
using OsX86.Tests.Storage;
using OsX86.Vm;

namespace OsX86.Tests.Vm;

/// <summary>
/// Work item 6b, without a browser: the boot image source on the image store (found, downloaded
/// from the catalog with its progress, refused), <see cref="BootImage"/> of a stored image on the
/// VM's write layer, the layer set kept with the VM and deleted with it, the serial console of a
/// profile without prefix, and what <c>x86 shell</c> prints about them.
/// </summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed class LinuxBaseDefaultTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Soon = VmTestRig.Soon;

    /// <summary>A linux-base like manifest: kernel, initramfs and rootfs compressed, rootfs on hda.</summary>
    internal static string LinuxBaseManifest(string image = "linux-base", string v86 = "0.5.473")
    {
        Dictionary<string, object?> manifest = TestManifests.ValidObject();
        manifest["image"] = image;
        manifest["version"] = "0.1.0";
        manifest["engine"] = new Dictionary<string, object?> { ["v86"] = v86 };
        manifest["files"] = new Dictionary<string, object?>
        {
            ["kernel"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = 1000, ["rawSize"] = 2000, ["blockSize"] = 1048576 },
            ["initramfs"] = new Dictionary<string, object?> { ["offset"] = 1000, ["size"] = 500, ["rawSize"] = 900, ["blockSize"] = 1048576 },
            ["rootfs"] = new Dictionary<string, object?> { ["offset"] = 1500, ["size"] = TestManifests.Size - 1500, ["rawSize"] = 130023424L, ["blockSize"] = 1048576 },
        };
        manifest["boot"] = new Dictionary<string, object?>
        {
            ["kind"] = "linux",
            ["cmdline"] = "root=/dev/sda rootfstype=ext4 ro console=ttyS0",
            ["disks"] = new Dictionary<string, object?> { ["hda"] = "rootfs" },
            ["readyMarker"] = "OSX86-READY",
            ["prompt"] = "osx86# ",
            ["memoryMiB"] = 256,
        };
        return TestManifests.Json(manifest);
    }

    private static readonly string Manifest = LinuxBaseManifest();

    private static ImageCatalog CatalogFor(string manifest) =>
        new([new KnownImage("linux-base", "0.1.0", "https://images.example/linux-base/0.1.0/manifest.json", SimulatedStorageWorkerChannel.Sha256Of(manifest))]);

    /// <summary>A worker where linux-base is not stored yet but can be downloaded.</summary>
    private static SimulatedStorageWorkerChannel MissingImage(string? served = null) => new()
    {
        ManifestText = served ?? Manifest,
        LocalManifests = { ["linux-base/0.1.0"] = served ?? Manifest },
    };

    private static object Stored(int format = 1, bool complete = true) =>
        new { image = "linux-base", version = "0.1.0", size = TestManifests.Size, bytesOnDisk = TestManifests.Size, complete, format, engine = new { v86 = "0.5.473" } };

    private sealed class Reports : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> All { get; } = [];

        public void Report(DownloadProgress value) => All.Add(value);
    }

    [Fact]
    public void A_stored_image_boots_its_located_files_on_the_layer_of_the_vm()
    {
        var boot = new BootImage(ImageManifest.Parse(Manifest));

        Assert.True(boot.HasDisks);
        Assert.Null(boot.KernelPath);
        Assert.Equal("root=/dev/sda rootfstype=ext4 ro console=ttyS0", boot.KernelCommandLine);
        Assert.Equal(["hda", "initramfs", "kernel"], boot.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new ImageFileLocation("osx86/images/linux-base/0.1.0/image.bin", 1500, TestManifests.Size - 1500), boot.Files["hda"]);
        Assert.Equal(1000, boot.Files["initramfs"].Offset);

        EngineStartOptions options = boot.CreateStartOptions(384, "vm-0123");
        Assert.Equal(384, options.MemoryMiB);
        Assert.Equal(EngineWriteLayerMode.Persistent, options.Disks["hda"].Layer.Mode);
        Assert.Equal("osx86/layers/vm-0123/hda.layer", options.Disks["hda"].Layer.Path);
        Assert.Equal(1500, options.Disks["hda"].Source.Offset);
        Assert.Equal(0, options.Kernel!.Offset);
        Assert.Throws<ArgumentException>(() => boot.CreateStartOptions(128, ""));
    }

    [Fact]
    public void Whole_files_boot_as_before_with_the_memory_of_the_profile()
    {
        var boot = new BootImage("osx86-test/vmlinuz", "osx86-test/initrd", "console=ttyS0");

        EngineStartOptions options = boot.CreateStartOptions(96, "vm-1");

        Assert.False(boot.HasDisks);
        Assert.Empty(boot.Files);
        Assert.Equal("osx86-test/vmlinuz", options.KernelPath);
        Assert.Equal("osx86-test/initrd", options.InitrdPath);
        Assert.Equal("console=ttyS0", options.KernelCommandLine);
        Assert.Equal(96, options.MemoryMiB);
        Assert.True(options.VirtioConsole);
        Assert.Empty(options.Disks);
        Assert.Throws<ArgumentException>(() => new BootImage("", "b", "c"));
    }

    [Fact]
    public async Task A_complete_image_in_the_store_is_used_without_download()
    {
        var worker = MissingImage();
        worker.Images.Add(Stored());
        await using var store = new ImageStore(worker);
        var source = new StoreBootImageSource(store, ImageCatalog.Empty);
        var reports = new Reports();

        BootImage boot = await source.ResolveAsync("linux-base", "0.1.0", reports, None);

        Assert.Equal("linux-base", boot.Manifest!.Image);
        Assert.Equal(["list", "read-manifest"], worker.PostedTypes);
        Assert.Empty(reports.All);
        Assert.Same(ImageCatalog.Empty, source.Catalog);
    }

    [Fact]
    public async Task A_missing_image_is_downloaded_from_the_catalog_with_its_progress()
    {
        var worker = MissingImage();
        worker.Images.Add(Stored(complete: false)); // a cut download: resumed, not refused
        await using var store = new ImageStore(worker);
        var source = new StoreBootImageSource(store, CatalogFor(Manifest));
        var reports = new Reports();

        BootImage boot = await source.ResolveAsync("linux-base", "0.1.0", reports, None);

        Assert.Equal(["list", "list", "fetch-manifest", "read-state", "download", "read-manifest"], worker.PostedTypes); // looked for again under the gate
        Assert.Equal("https://images.example/linux-base/0.1.0/", worker.LastDownload!.Value.GetProperty("baseUrl").GetString());
        Assert.Equal(DownloadPhase.FetchingManifest, reports.All[0].Phase);
        Assert.Equal(DownloadPhase.Completed, reports.All[^1].Phase);
        Assert.Contains(reports.All, report => report.Phase == DownloadPhase.Downloading && report.Fraction is > 0.4 and < 0.6);
        Assert.Equal(TestManifests.Size - 1500, boot.Files["hda"].Size);
    }

    [Fact]
    public async Task An_image_the_store_lacks_and_the_catalog_does_not_list_is_unavailable()
    {
        var worker = MissingImage();
        await using var store = new ImageStore(worker);
        var source = new StoreBootImageSource(store, ImageCatalog.Empty);

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await source.ResolveAsync("linux-base", "0.1.0", null, None));

        Assert.Equal(X86ErrorCodes.ImageUnavailable, error.Code);
        Assert.Contains("no origin is configured", error.Message, StringComparison.Ordinal);
        Assert.Equal(["list", "list"], worker.PostedTypes);
        Assert.Equal(X86ErrorCodes.ImageUnavailable, (await Assert.ThrowsAsync<X86Exception>(async () => await source.ResolveAsync("Not An Id", "0.1.0", null, None))).Code);
        Assert.Equal(X86ErrorCodes.ImageUnavailable, (await Assert.ThrowsAsync<X86Exception>(async () => await source.ResolveAsync("linux-base", "latest", null, None))).Code);
    }

    [Fact]
    public async Task A_failed_download_an_unreadable_store_format_and_a_wrong_origin_are_reported()
    {
        var failing = MissingImage();
        failing.DownloadError = ("chunk-integrity", "chunk 1 does not hash to the manifest");
        await using (var store = new ImageStore(failing))
        {
            X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await new StoreBootImageSource(store, CatalogFor(Manifest)).ResolveAsync("linux-base", "0.1.0", null, None));
            Assert.Equal(X86ErrorCodes.ImageUnavailable, error.Code);
            Assert.Contains("chunk-integrity", error.Message, StringComparison.Ordinal);
        }

        var future = MissingImage();
        future.Images.Add(Stored(format: 9));
        await using (var store = new ImageStore(future))
        {
            X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await new StoreBootImageSource(store, CatalogFor(Manifest)).ResolveAsync("linux-base", "0.1.0", null, None));
            Assert.Contains("format 9", error.Message, StringComparison.Ordinal);
        }

        string other = LinuxBaseManifest(image: "other-image");
        var wrong = MissingImage(other);
        await using (var store = new ImageStore(wrong))
        {
            X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await new StoreBootImageSource(store, CatalogFor(other)).ResolveAsync("linux-base", "0.1.0", null, None));
            Assert.Contains("serves other-image 0.1.0", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_layers_of_a_vm_are_deleted_unless_an_engine_holds_them()
    {
        var worker = MissingImage();
        worker.Layers["vm-1"] = [new { disk = "hda", bytes = 4096, inUse = false }];
        worker.LockedLayerSets.Add("vm-2");
        await using var store = new ImageStore(worker);
        var source = new StoreBootImageSource(store, ImageCatalog.Empty);

        await source.DeleteLayersAsync("vm-1", None);
        await source.DeleteLayersAsync("vm-3", None); // none: nothing to do

        Assert.False(worker.Layers.ContainsKey("vm-1"));
        Assert.Equal(X86ErrorCodes.VmBusy, (await Assert.ThrowsAsync<X86Exception>(async () => await source.DeleteLayersAsync("vm-2", None))).Code);
        await StaticBootImageSource.Empty.DeleteLayersAsync("vm-1", None);
    }

    [Fact]
    public async Task A_layer_set_is_given_at_creation_kept_on_rename_and_checked_when_read()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync("base", BuiltInProfiles.LinuxBase);
        Assert.Equal($"vm-{vm.Id:N}", vm.Record.LayerSet);
        Assert.Equal(vm.Record.LayerSet, vm.Record.EffectiveLayerSet);
        Assert.True(StorageLayout.IsValidImageId(vm.Record.LayerSet));

        await rig.Manager.RenameAsync(VmTestRig.User, vm.Id, "renamed", None);
        Assert.Equal($"vm-{vm.Id:N}", (await rig.Manager.ResolveAsync(VmTestRig.User, "renamed", None)).LayerSet);

        // A VM registered before work item 6b has none: its identifier gives it.
        var old = new VmRecord { Id = vm.Id, Name = "old", Created = DateTimeOffset.UnixEpoch, Profile = BuiltInProfiles.LinuxBase };
        Assert.Null(old.LayerSet);
        Assert.Equal($"vm-{vm.Id:N}", old.EffectiveLayerSet);

        string text = new VmRegistryDocument { User = VmTestRig.User, Vms = [vm.Record] }.ToJson();
        Assert.Contains("\"layerSet\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("effectiveLayerSet", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(vm.Record.LayerSet, VmRegistryDocument.Parse(text, VmTestRig.User).Vms[0].LayerSet);
        string broken = text.Replace($"vm-{vm.Id:N}", "Not/A Set", StringComparison.Ordinal);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => VmRegistryDocument.Parse(broken, VmTestRig.User)).Code);
    }

    /// <summary>A manager whose images come from a simulated store, on simulated engines.</summary>
    private static (VmManager Manager, FakeEngineFactory Engines, ImageStore Store) StoreManager(SimulatedStorageWorkerChannel worker, ImageCatalog catalog)
    {
        var store = new ImageStore(worker);
        var engines = new FakeEngineFactory();
        var manager = new VmManager(new InMemoryVmRegistryStore(), engines, new StoreBootImageSource(store, catalog), new FixedHostMemory(1L << 30), VmTestRig.FastOptions);
        return (manager, engines, store);
    }

    [Fact]
    public async Task Linux_base_starts_on_its_own_layer_and_its_layer_goes_with_the_vm()
    {
        var worker = MissingImage();
        (VmManager manager, FakeEngineFactory engines, ImageStore store) = StoreManager(worker, CatalogFor(Manifest));
        await using (store)
        await using (manager)
        {
            var progress = new List<DownloadProgress>();
            manager.ImageProgress += (_, e) => progress.Add(e.Data);
            VmSummary summary = await manager.CreateAsync(VmTestRig.User, "base", BuiltInProfiles.LinuxBase, None);
            string layerSet = VmRecord.LayerSetOf(summary.Id);

            await manager.HoldAsync(VmTestRig.User, summary.Id, None);
            EngineStartOptions started = engines.Last.StartedWith!;
            Assert.Equal(256, started.MemoryMiB);
            Assert.Equal($"osx86/layers/{layerSet}/hda.layer", started.Disks["hda"].Layer.Path);
            Assert.Equal(DownloadPhase.Completed, progress[^1].Phase);

            // Stopped and started again: the image is there, nothing is downloaded, the same layer.
            await manager.StopAsync(VmTestRig.User, summary.Id, force: false, None);
            worker.Images.Add(Stored());
            progress.Clear();
            await manager.HoldAsync(VmTestRig.User, summary.Id, None);
            Assert.Empty(progress);
            Assert.Equal($"osx86/layers/{layerSet}/hda.layer", engines.Last.StartedWith!.Disks["hda"].Layer.Path);

            // A layer still held refuses the removal and the VM stays; once free it goes with the VM.
            worker.Layers[layerSet] = [new { disk = "hda", bytes = 8192, inUse = false }];
            worker.LockedLayerSets.Add(layerSet);
            Assert.Equal(X86ErrorCodes.VmBusy, (await Assert.ThrowsAsync<X86Exception>(async () => await manager.RemoveAsync(VmTestRig.User, summary.Id, force: true, None))).Code);
            Assert.Single(await manager.ListAsync(VmTestRig.User, None));
            worker.LockedLayerSets.Clear();
            await manager.RemoveAsync(VmTestRig.User, summary.Id, force: false, None);
            Assert.Empty(await manager.ListAsync(VmTestRig.User, None));
            Assert.False(worker.Layers.ContainsKey(layerSet));
            Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await manager.RemoveAsync(VmTestRig.User, summary.Id, force: false, None))).Code);
        }
    }

    [Fact]
    public async Task An_image_built_for_another_v86_is_refused_before_any_engine()
    {
        var worker = MissingImage(LinuxBaseManifest(v86: "0.4.100"));
        worker.Images.Add(Stored());
        (VmManager manager, FakeEngineFactory engines, ImageStore store) = StoreManager(worker, ImageCatalog.Empty);
        await using (store)
        await using (manager)
        {
            VmSummary summary = await manager.CreateAsync(VmTestRig.User, "base", BuiltInProfiles.LinuxBase, None);
            X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await manager.HoldAsync(VmTestRig.User, summary.Id, None));
            Assert.Equal(X86ErrorCodes.ImageUnavailable, error.Code);
            Assert.Contains("engine-version-mismatch", error.Message, StringComparison.Ordinal);
            Assert.Empty(engines.Created);
        }
    }

    [Fact]
    public async Task Without_prefix_linux_base_opens_its_serial_console_and_with_one_the_agent_shell()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => VmTestRig.ScriptConsole(engine, prefix: "work");
        VmInstance vm = await rig.CreateAsync("base", BuiltInProfiles.LinuxBase);

        IVmConsole serial = await vm.OpenConsoleAsync(new VmConsoleOptions(), None);
        Assert.True(serial.IsSerial);
        Assert.True(vm.HasAgent); // the agent runs; linux-base just has no guest user for it yet
        Assert.Empty(vm.Children);

        IVmConsole agent = await vm.OpenConsoleAsync(new VmConsoleOptions { Prefix = "work" }, None);
        Assert.False(agent.IsSerial);
        Assert.Equal("work", rig.Engines.Last.Agent!.Request("open-console").GetProperty("prefix").GetString());
        await agent.DisposeAsync();
        await serial.DisposeAsync();
        Assert.Equal(0, await serial.Exited.WaitAsync(Soon));
    }

    [Fact]
    public async Task X86_shell_reports_the_download_and_says_it_opened_the_serial_console()
    {
        var worker = MissingImage();
        (VmManager manager, _, ImageStore store) = StoreManager(worker, CatalogFor(Manifest));
        await using (store)
        await using (manager)
        {
            var services = new X86CommandServices(manager, new X86CommandTests.FakeFiles());
            X86Command.ServicesFactory = () => services;
            try
            {
                var created = new StringWriter();
                Assert.Equal(0, await new X86Command().ExecuteAsync(TestAppContext.Terminal(["vm", "create", "base"], created, new StringWriter()), None));
                Assert.Contains("from the profile linux-base", created.ToString(), StringComparison.Ordinal);

                var stdout = new StringWriter();
                var stderr = new StringWriter();
                int code = await new X86Command().ExecuteAsync(TestAppContext.Terminal(["shell"], stdout, stderr, new StringReader("~.\n")), None);

                string[] lines = stderr.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
                Assert.Equal(0, code);
                Assert.Equal("x86: linux-base 0.1.0 is not on this machine yet: downloading it.", lines[0]);
                Assert.Equal("x86: downloading linux-base 0.1.0: 0 % (0.0 of 100.0 MiB)", lines[1]);
                Assert.Equal("x86: downloading linux-base 0.1.0: 49 % (50.0 of 100.0 MiB, 1.0 MiB/s)", lines[2]);
                Assert.Equal("x86: linux-base 0.1.0 downloaded and verified (100.0 MiB).", lines[3]);
                Assert.Equal(X86Command.SerialNotice, lines[4]);
                Assert.Equal(5, lines.Length);
            }
            finally
            {
                X86Command.ServicesFactory = static () => X86Runtime.Services;
            }
        }
    }

    [Fact]
    public void The_reporter_writes_each_phase_once_and_a_line_every_ten_percent()
    {
        var reporter = new DownloadReporter(TextWriter.Null);
        static DownloadProgress At(DownloadPhase phase, long verified) => new(phase, verified, 1000, 0, 4, 0);

        Assert.Equal("x86: a 1.0.0 is not on this machine yet: downloading it.", reporter.Format("a 1.0.0", At(DownloadPhase.FetchingManifest, 0)));
        Assert.Null(reporter.Format("a 1.0.0", At(DownloadPhase.FetchingManifest, 0)));
        Assert.Null(reporter.Format("a 1.0.0", At(DownloadPhase.Planning, 0)));
        Assert.Equal("x86: downloading a 1.0.0: 0 % (0.0 of 0.0 MiB)", reporter.Format("a 1.0.0", At(DownloadPhase.Downloading, 0)));
        Assert.Null(reporter.Format("a 1.0.0", At(DownloadPhase.Downloading, 90)));
        Assert.StartsWith("x86: downloading a 1.0.0: 25 %", reporter.Format("a 1.0.0", At(DownloadPhase.Downloading, 250)), StringComparison.Ordinal);
        Assert.Contains("failed", reporter.Format("a 1.0.0", At(DownloadPhase.Failed, 250)), StringComparison.Ordinal);
        Assert.Contains("interrupted", reporter.Format("a 1.0.0", At(DownloadPhase.Cancelled, 250)), StringComparison.Ordinal);
        Assert.Null(reporter.Format("a 1.0.0", At(DownloadPhase.Idle, 0)));
    }

    [Fact]
    public async Task A_reporter_whose_terminal_went_away_lets_the_download_go_on()
    {
        var reporter = new DownloadReporter(new BrokenWriter());
        reporter.Report(BuiltInProfiles.LinuxBase, new DownloadProgress(DownloadPhase.FetchingManifest, 0, 0, 0, 0, 0));
        await reporter.CompleteAsync().WaitAsync(Soon);
        await new DownloadReporter(TextWriter.Null).CompleteAsync().WaitAsync(Soon); // never started
    }

    [Fact]
    public async Task The_host_environment_is_installed_once_and_read_by_the_command()
    {
        try
        {
            Assert.Throws<ArgumentNullException>(() => VmHost.Configure(null!));
            VmHostEnvironment browser = VmHost.ForBrowser(new NoJsRuntime(), ImageCatalog.Empty);
            VmHost.Configure(browser);

            Assert.Same(browser, VmHost.Current);
            Assert.IsType<StoreBootImageSource>(browser.Images);
            Assert.Same(FixedHostMemory.Unknown, browser.Memory);
            IEngine engine = Assert.IsType<JsEngineFactory>(browser.Engines).Create();
            Assert.IsType<OsX86.Engine.V86.V86Engine>(engine);
            await engine.DisposeAsync();
            Assert.Throws<ArgumentNullException>(() => new JsEngineFactory(null!));
        }
        finally
        {
            VmHost.Clear();
        }

        Assert.Null(VmHost.Current);
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => throw new IOException("the terminal is gone");
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
