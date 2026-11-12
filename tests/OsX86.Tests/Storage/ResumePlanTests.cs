using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary><see cref="ResumePlan"/>: what a download fetches after a cut, a reload, a changed manifest or a corrupt state.</summary>
[Trait("Category", "unit")]
public sealed class ResumePlanTests
{
    private static readonly ImageManifest Manifest = ImageManifest.Parse(TestManifests.Valid());

    [Fact]
    public void Nothing_stored_fetches_everything()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, null);

        Assert.Equal([0, 1, 2, 3], plan.ChunksToFetch);
        Assert.Empty(plan.ChunksVerified);
        Assert.False(plan.Reset);
        Assert.False(plan.IsComplete);
        Assert.Equal(0, plan.BytesVerified);
        Assert.Equal(Manifest.Size, plan.BytesToFetch);
        Assert.Same(Manifest, plan.Manifest);
    }

    [Fact]
    public void A_state_without_files_fetches_everything()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(null, null, false));
        Assert.Equal([0, 1, 2, 3], plan.ChunksToFetch);
        Assert.False(plan.Reset);
    }

    [Fact]
    public void Verified_chunks_are_kept_and_the_rest_fetched_in_order()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(Manifest.Sha256, [3, 0, 0], false));

        Assert.Equal([0, 3], plan.ChunksVerified);
        Assert.Equal([1, 2], plan.ChunksToFetch);
        Assert.False(plan.Reset);
        Assert.Equal(Manifest.ChunkLength(0) + Manifest.ChunkLength(3), plan.BytesVerified);
        Assert.Equal(2L * Manifest.ChunkSize, plan.BytesToFetch);
        Assert.Contains("2 of 4", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_chunk_verified_leaves_nothing_to_fetch()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(Manifest.Sha256, [0, 1, 2, 3], false));

        Assert.True(plan.IsComplete);
        Assert.Empty(plan.ChunksToFetch);
        Assert.Equal(Manifest.Size, plan.BytesVerified);
    }

    [Fact]
    public void A_complete_image_leaves_nothing_to_fetch()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(Manifest.Sha256, [0, 1, 2, 3], true));

        Assert.True(plan.IsComplete);
        Assert.Equal([0, 1, 2, 3], plan.ChunksVerified);
        Assert.Contains("complete", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_manifest_resets_the_state_and_fetches_everything()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(new string('0', 64), [0, 1, 2, 3], true));

        Assert.True(plan.Reset);
        Assert.Equal([0, 1, 2, 3], plan.ChunksToFetch);
        Assert.Empty(plan.ChunksVerified);
        Assert.Contains("another manifest", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_without_a_manifest_hash_resets()
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(null, [0], false));
        Assert.True(plan.Reset);
        Assert.Contains("no manifest", plan.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public void A_chunk_outside_the_manifest_resets(int index)
    {
        ResumePlan plan = ResumePlan.Create(Manifest, new DownloadStateSnapshot(Manifest.Sha256, [0, index], false));

        Assert.True(plan.Reset);
        Assert.Equal([0, 1, 2, 3], plan.ChunksToFetch);
        Assert.Contains($"chunk {index}", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_manifest_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ResumePlan.Create(null!, null));
    }
}
