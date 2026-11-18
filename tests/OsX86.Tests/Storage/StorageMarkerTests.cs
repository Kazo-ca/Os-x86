using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>
/// What the storage worker does for ADR 0250 of Kazo (decision 289 A, work item 8): it writes the
/// marker file Kazo checks at each boot, and measures the whole <c>osx86/</c> tree the Feature
/// declares to Kazo.
/// </summary>
[Trait("Category", "unit")]
public sealed class StorageMarkerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_marker_is_written_at_the_root_of_the_feature_tree()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var store = new ImageStore(worker);

        await store.WriteMarkerAsync(None);

        Assert.Equal("osx86/.kazoos-marker", StorageLayout.MarkerPath);
        string posted = Assert.Single(worker.Posted, message => message.Contains("\"write-marker\"", StringComparison.Ordinal));
        using JsonDocument command = JsonDocument.Parse(posted);
        using JsonDocument marker = JsonDocument.Parse(command.RootElement.GetProperty("text").GetString()!);
        Assert.Equal(1, marker.RootElement.GetProperty("format").GetInt32());
        Assert.Equal("ca.kazo.x86", marker.RootElement.GetProperty("feature").GetString());
        Assert.Equal(1, worker.MarkersWritten);
    }

    [Fact]
    public async Task The_estimate_gives_the_bytes_of_the_whole_feature_tree()
    {
        var worker = new SimulatedStorageWorkerChannel { FeatureBytes = 9876 };
        await using var store = new ImageStore(worker);

        StorageEstimate estimate = await store.EstimateAsync(None);

        Assert.Equal(1234, estimate.RepositoryBytes);
        Assert.Equal(9876, estimate.FeatureBytes);
    }

    [Fact]
    public async Task An_older_worker_without_the_tree_size_falls_back_on_the_images()
    {
        var worker = new SimulatedStorageWorkerChannel { FeatureBytes = null };
        await using var store = new ImageStore(worker);

        StorageEstimate estimate = await store.EstimateAsync(None);

        Assert.Equal(estimate.RepositoryBytes, estimate.FeatureBytes);
    }
}
