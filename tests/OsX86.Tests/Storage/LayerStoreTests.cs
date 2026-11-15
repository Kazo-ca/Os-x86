using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>Work item 6a: the size and the deletion of the write layers, through <see cref="ImageStore"/> and the storage worker protocol.</summary>
[Trait("Category", "unit")]
public sealed class LayerStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_layers_of_a_set_are_listed_with_their_size_and_metadata()
    {
        var worker = new SimulatedStorageWorkerChannel();
        string metadata = StoredObjectMetadata.ForLayer(ImageManifest.Parse(TestManifests.Valid()), "0.5.473", DateTimeOffset.UnixEpoch).ToJson();
        worker.Layers["vm-1"] =
        [
            new { disk = "hda", bytes = 12288, inUse = false, metadata },
            new { disk = "hdb", bytes = (long?)null, inUse = true, metadata = "{not json" },
        ];
        await using var store = new ImageStore(worker);

        IReadOnlyList<StoredLayer> layers = await store.ListLayersAsync("vm-1", None);

        StoredLayer hda = Assert.Single(layers, layer => layer.Disk == "hda");
        Assert.Equal(12288, hda.Bytes);
        Assert.False(hda.InUse);
        Assert.Equal(StoredObjectKind.Layer, hda.Metadata!.Kind);
        StoredLayer hdb = Assert.Single(layers, layer => layer.Disk == "hdb");
        Assert.Null(hdb.Bytes);
        Assert.True(hdb.InUse);
        Assert.Null(hdb.Metadata); // unreadable: listed so that it can be deleted
        Assert.Empty(await store.ListLayersAsync("vm-2", None));
        using JsonDocument command = JsonDocument.Parse(worker.Posted.Last());
        Assert.Equal("list-layers", command.RootElement.GetProperty("type").GetString());
        Assert.Equal("vm-2", command.RootElement.GetProperty("set").GetString());
    }

    [Fact]
    public async Task Deleting_the_layers_of_a_set_is_refused_while_an_engine_runs_on_them()
    {
        var worker = new SimulatedStorageWorkerChannel();
        worker.Layers["vm-1"] = [new { disk = "hda", bytes = 4096, inUse = false }];
        worker.Layers["vm-busy"] = [new { disk = "hda", bytes = (long?)null, inUse = true }];
        worker.LockedLayerSets.Add("vm-busy");
        await using var store = new ImageStore(worker);

        await store.DeleteLayersAsync("vm-1", None);
        Assert.Empty(await store.ListLayersAsync("vm-1", None));
        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.DeleteLayersAsync("vm-busy", None));
        Assert.Equal(StorageErrorCodes.Locked, error.Code);
        Assert.Single(await store.ListLayersAsync("vm-busy", None));
    }

    [Fact]
    public async Task A_bad_layer_set_name_is_refused_before_the_worker_starts()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var store = new ImageStore(worker);
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.ListLayersAsync("../x", None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.DeleteLayersAsync("Vm", None));
        Assert.False(worker.Started);
    }
}
