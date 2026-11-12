using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary><see cref="ImageStore"/> against a simulated worker: the download state machine, resume, refusals, cancellation, repository operations.</summary>
[Trait("Category", "unit")]
public sealed class ImageStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly string Manifest = TestManifests.Valid();
    private static readonly ImageSource Source = new("https://images.example/alpine-test/1.0.0/", SimulatedStorageWorkerChannel.Sha256Of(Manifest));

    private sealed class Progress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task A_download_goes_through_every_phase_and_marks_the_image_complete()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest };
        await using var store = new ImageStore(worker, new FakeTime());
        var progress = new Progress();

        StoredImage image = await store.DownloadAsync(Source, progress, None);

        Assert.Equal(["fetch-manifest", "read-state", "download"], worker.PostedTypes);
        Assert.Equal([DownloadPhase.FetchingManifest, DownloadPhase.Planning, DownloadPhase.Downloading, DownloadPhase.Downloading, DownloadPhase.Completed], progress.Reports.Select(r => r.Phase));
        Assert.True(progress.Reports.Select(r => r.Phase).Zip(progress.Reports.Skip(1).Select(r => r.Phase)).All(pair => pair.First == pair.Second || DownloadStateMachine.CanTransition(pair.First, pair.Second)));
        Assert.Equal(1_000_000, progress.Reports[3].BytesPerSecond);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
        Assert.Equal("alpine-test", image.Image);
        Assert.Equal("1.0.0", image.Version);
        Assert.True(image.Complete);
        Assert.True(image.IsSupported);
        Assert.Equal(TestManifests.Size + 500, image.BytesOnDisk);
        Assert.Equal("osx86/images/alpine-test/1.0.0/image.bin", image.PayloadPath);
        Assert.True(store.OpfsAvailable);
        Assert.True(store.WebLocksAvailable);

        JsonElement command = worker.LastDownload!.Value;
        Assert.Equal(Source.BaseUrl, command.GetProperty("baseUrl").GetString());
        Assert.Equal(Manifest, command.GetProperty("manifest").GetString());
        Assert.Equal([0, 1, 2, 3], command.GetProperty("chunks").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(command.GetProperty("reset").GetBoolean());
        Assert.Equal(2, command.GetProperty("parallelism").GetInt32());
        StoredObjectMetadata metadata = StoredObjectMetadata.Parse(command.GetProperty("metadata").GetString()!);
        Assert.Equal(FakeTime.Now, metadata.Created);
        Assert.False(metadata.Complete);
    }

    [Fact]
    public async Task A_resumed_download_asks_only_for_the_missing_chunks()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest };
        worker.States["alpine-test/1.0.0"] = new DownloadStateSnapshot(SimulatedStorageWorkerChannel.Sha256Of(Manifest), [0, 2], false);
        await using var store = new ImageStore(worker);
        var progress = new Progress();

        await store.DownloadAsync(Source, progress, None);

        Assert.Equal([1, 3], worker.LastDownload!.Value.GetProperty("chunks").EnumerateArray().Select(e => e.GetInt32()));
        DownloadProgress start = progress.Reports.First(r => r.Phase == DownloadPhase.Downloading);
        Assert.Equal(2, start.ChunksVerified);
        Assert.Equal(2L * ImageManifest.DefaultChunkSize, start.BytesVerified);
    }

    [Fact]
    public async Task A_state_of_another_manifest_resets_the_download()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest };
        worker.States["alpine-test/1.0.0"] = new DownloadStateSnapshot(new string('1', 64), [0, 1, 2, 3], true);
        await using var store = new ImageStore(worker);

        await store.DownloadAsync(Source, null, None);

        Assert.True(worker.LastDownload!.Value.GetProperty("reset").GetBoolean());
        Assert.Equal(4, worker.LastDownload!.Value.GetProperty("chunks").GetArrayLength());
    }

    [Fact]
    public async Task A_complete_image_sends_an_empty_download_that_only_confirms()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest };
        worker.States["alpine-test/1.0.0"] = new DownloadStateSnapshot(SimulatedStorageWorkerChannel.Sha256Of(Manifest), [0, 1, 2, 3], true);
        await using var store = new ImageStore(worker);

        StoredImage image = await store.DownloadAsync(Source, null, None);

        Assert.True(image.Complete);
        Assert.Equal(0, worker.LastDownload!.Value.GetProperty("chunks").GetArrayLength());
    }

    [Fact]
    public async Task A_manifest_with_the_wrong_hash_is_refused_and_nothing_is_written()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest };
        await using var store = new ImageStore(worker);
        var progress = new Progress();

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.DownloadAsync(Source with { ManifestSha256 = new string('0', 64) }, progress, None));

        Assert.Equal(StorageErrorCodes.ManifestIntegrity, error.Code);
        Assert.Equal(["fetch-manifest"], worker.PostedTypes);
        Assert.DoesNotContain(progress.Reports, r => DownloadStateMachine.MayHaveWritten(r.Phase));
        Assert.Equal(DownloadPhase.Failed, progress.Reports[^1].Phase);
    }

    [Fact]
    public async Task A_cancellation_that_lands_while_the_download_command_is_being_posted_still_tells_the_worker()
    {
        using var cancellation = new CancellationTokenSource();
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest, HangDownloads = true, PostDelay = TimeSpan.FromMilliseconds(30), CancelOnDownloadPost = cancellation };
        await using var store = new ImageStore(worker);
        var progress = new Progress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.DownloadAsync(Source, progress, cancellation.Token));

        Assert.Equal(["fetch-manifest", "read-state", "download", "cancel"], worker.PostedTypes);
        Assert.Equal(DownloadPhase.Cancelled, progress.Reports[^1].Phase);
    }

    [Fact]
    public async Task A_throwing_progress_callback_does_not_break_the_download()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest };
        await using var store = new ImageStore(worker);
        var reported = new List<StorageException>();
        store.UnroutedErrorReported += (_, e) => reported.Add(e);
        var progress = new ThrowingProgress();

        StoredImage image = await store.DownloadAsync(Source, progress, None);

        Assert.True(image.Complete);
        Assert.Single(reported);
        Assert.Contains("progress callback threw", reported[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_worker_crash_every_later_request_fails_at_once()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var store = new ImageStore(worker);
        await store.EstimateAsync(None);
        await worker.SendAsync(new { type = "error", code = "worker-error", message = "crashed" });

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.ListAsync(None));

        Assert.Equal(StorageErrorCodes.WorkerError, error.Code);
        Assert.Contains("crashed", error.Message, StringComparison.Ordinal);
        Assert.Equal(["estimate"], worker.PostedTypes);
    }

    [Fact]
    public async Task A_failed_start_is_retried_by_the_next_operation()
    {
        var worker = new SimulatedStorageWorkerChannel { StartException = new InvalidOperationException("module 404") };
        await using var store = new ImageStore(worker);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ListAsync(None));

        worker.StartException = null;
        IReadOnlyList<StoredImage> images = await store.ListAsync(None);

        Assert.Empty(images);
        Assert.True(worker.Started);
    }

    private sealed class ThrowingProgress : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            if (value.Phase == DownloadPhase.Downloading && value.BytesPerSecond > 0)
            {
                throw new InvalidOperationException("UI gone");
            }
        }
    }

    [Fact]
    public async Task The_store_checks_the_hash_itself_even_if_the_worker_did_not()
    {
        var worker = new SimulatedStorageWorkerChannel { Silent = true };
        await using var store = new ImageStore(worker);
        await worker.SendAsync(new { type = "ready", opfs = true, locks = true });

        ValueTask<StoredImage> download = store.DownloadAsync(Source, null, None);
        await worker.SendAsync(new { type = "manifest", id = 1, text = TestManifests.With("format", 1).Replace("alpine-test", "alpine-tesu", StringComparison.Ordinal) });

        var error = await Assert.ThrowsAsync<StorageException>(async () => await download);
        Assert.Equal(StorageErrorCodes.ManifestIntegrity, error.Code);
        Assert.Equal(["fetch-manifest"], worker.PostedTypes);
    }

    [Fact]
    public async Task An_unknown_format_is_refused_before_the_download_and_nothing_is_written()
    {
        string manifest = TestManifests.With("format", 2);
        var worker = new SimulatedStorageWorkerChannel { ManifestText = manifest };
        await using var store = new ImageStore(worker);
        var progress = new Progress();

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.DownloadAsync(Source with { ManifestSha256 = SimulatedStorageWorkerChannel.Sha256Of(manifest) }, progress, None));

        Assert.Equal(StorageErrorCodes.FormatUnsupported, error.Code);
        Assert.Contains("format 2", error.Message, StringComparison.Ordinal);
        Assert.Equal(["fetch-manifest"], worker.PostedTypes);
        Assert.Null(worker.LastDownload);
    }

    [Fact]
    public async Task An_invalid_manifest_is_refused_before_the_download()
    {
        string manifest = TestManifests.With("chunks", new List<object?>());
        var worker = new SimulatedStorageWorkerChannel { ManifestText = manifest };
        await using var store = new ImageStore(worker);

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.DownloadAsync(Source with { ManifestSha256 = SimulatedStorageWorkerChannel.Sha256Of(manifest) }, null, None));

        Assert.Equal(StorageErrorCodes.ManifestInvalid, error.Code);
        Assert.Null(worker.LastDownload);
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_fetched_fails_with_its_code()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = null };
        await using var store = new ImageStore(worker);

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.DownloadAsync(Source, null, None));

        Assert.Equal(StorageErrorCodes.ManifestFetchFailed, error.Code);
        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_error_during_the_download_fails_it_and_reports_the_failure()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest, DownloadError = ("network", "chunk-00002.bin: HTTP 503 (attempt 8 of 8)") };
        await using var store = new ImageStore(worker);
        var progress = new Progress();

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.DownloadAsync(Source, progress, None));

        Assert.Equal(StorageErrorCodes.Network, error.Code);
        Assert.Contains("chunk-00002.bin", error.Message, StringComparison.Ordinal);
        Assert.Equal(DownloadPhase.Failed, progress.Reports[^1].Phase);
    }

    [Fact]
    public async Task Cancelling_tells_the_worker_and_keeps_the_verified_chunks()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest, HangDownloads = true };
        await using var store = new ImageStore(worker);
        using var cancellation = new CancellationTokenSource();
        var progress = new Progress();

        ValueTask<StoredImage> download = store.DownloadAsync(Source, progress, cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
        Assert.Equal(["fetch-manifest", "read-state", "download", "cancel"], worker.PostedTypes);
        Assert.Equal(DownloadPhase.Cancelled, progress.Reports[^1].Phase);
    }

    [Fact]
    public async Task A_cancel_the_worker_never_acknowledges_still_returns_after_the_grace_period()
    {
        var time = new FakeTime();
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest, HangDownloads = true, IgnoreCancel = true };
        await using var store = new ImageStore(worker, time);
        using var cancellation = new CancellationTokenSource();

        ValueTask<StoredImage> download = store.DownloadAsync(Source, null, cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
        Assert.Contains("cancel", worker.PostedTypes);
    }

    [Fact]
    public async Task A_worker_that_reports_cancelled_on_its_own_cancels_the_operation()
    {
        var worker = new SimulatedStorageWorkerChannel { ManifestText = Manifest, DownloadError = ("cancelled", "the download was cancelled") };
        await using var store = new ImageStore(worker);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.DownloadAsync(Source, null, None));
    }

    [Fact]
    public async Task Without_the_opfs_every_operation_fails_with_opfs_unavailable()
    {
        var worker = new SimulatedStorageWorkerChannel { ReadyOpfs = false };
        await using var store = new ImageStore(worker);

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.ListAsync(None));

        Assert.Equal(StorageErrorCodes.OpfsUnavailable, error.Code);
        Assert.False(store.OpfsAvailable);
        Assert.Empty(worker.Posted);
    }

    [Fact]
    public async Task A_worker_that_fails_before_ready_fails_the_first_operation()
    {
        var worker = new SimulatedStorageWorkerChannel { SendReady = false, Silent = true };
        await using var store = new ImageStore(worker);

        ValueTask<IReadOnlyList<StoredImage>> list = store.ListAsync(None);
        await worker.SendAsync(new { type = "error", code = "worker-error", message = "script failed to load" });

        var error = await Assert.ThrowsAsync<StorageException>(async () => await list);
        Assert.Equal(StorageErrorCodes.WorkerError, error.Code);
    }

    [Fact]
    public async Task A_worker_error_without_id_after_ready_fails_the_pending_requests_and_is_reported()
    {
        var worker = new SimulatedStorageWorkerChannel { Silent = true, ReadyLocks = false };
        await using var store = new ImageStore(worker);
        StorageException? reported = null;
        store.UnroutedErrorReported += (_, e) => reported = e;

        ValueTask<IReadOnlyList<StoredImage>> list = store.ListAsync(None);
        await worker.SendAsync(new { type = "error", message = "crashed" });

        var error = await Assert.ThrowsAsync<StorageException>(async () => await list);
        Assert.Equal(StorageErrorCodes.WorkerError, error.Code);
        Assert.Same(error, reported);
        Assert.False(store.WebLocksAvailable);
    }

    [Fact]
    public async Task A_message_that_is_not_the_protocol_is_reported_and_ignored()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var store = new ImageStore(worker);
        var reported = new List<StorageException>();
        store.UnroutedErrorReported += (_, e) => reported.Add(e);
        await store.EstimateAsync(None);

        await worker.SendRawAsync("not json");
        await worker.SendRawAsync("{}");
        await worker.SendAsync(new { type = "images", id = 999 });
        await worker.SendAsync(new { type = "images" });

        Assert.Equal(2, reported.Count);
        Assert.All(reported, e => Assert.Equal(StorageErrorCodes.Protocol, e.Code));
    }

    [Fact]
    public async Task An_unexpected_answer_type_is_a_protocol_error()
    {
        var worker = new SimulatedStorageWorkerChannel { Silent = true };
        await using var store = new ImageStore(worker);
        await worker.SendAsync(new { type = "ready", opfs = true, locks = true });

        ValueTask<StorageEstimate> estimate = store.EstimateAsync(None);
        await worker.SendAsync(new { type = "images", id = 1 });

        var error = await Assert.ThrowsAsync<StorageException>(async () => await estimate);
        Assert.Equal(StorageErrorCodes.Protocol, error.Code);
        Assert.Contains("'estimate'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_estimate_and_delete_reach_the_worker()
    {
        var worker = new SimulatedStorageWorkerChannel();
        worker.Images.Add(new { image = "alpine-test", version = "1.0.0", size = 100, bytesOnDisk = 120, complete = true, format = 1, engine = new { v86 = "0.5.473" } });
        worker.Images.Add(new { image = "old", version = "0.9.0", size = 50, bytesOnDisk = 60, complete = false, format = 2 });
        await using var store = new ImageStore(worker);

        IReadOnlyList<StoredImage> images = await store.ListAsync(None);
        StorageEstimate estimate = await store.EstimateAsync(None);
        await store.DeleteAsync("old", "0.9.0", None);

        Assert.Equal(2, images.Count);
        Assert.Equal("0.5.473", images[0].Engine["v86"]);
        Assert.True(images[0].IsSupported);
        Assert.False(images[1].IsSupported);
        Assert.Empty(images[1].Engine);
        Assert.Equal(new StorageEstimate(5000, 10_000_000, 1234), estimate);
        Assert.Equal(["list", "estimate", "delete"], worker.PostedTypes);
        Assert.Contains("\"image\":\"old\"", worker.Posted[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_are_checked_before_reaching_the_worker()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var store = new ImageStore(worker);

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.DeleteAsync("../x", "1.0.0", None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReadManifestAsync("alpine-test", "latest", None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.DownloadAsync(Source with { BaseUrl = "no" }, null, None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.DownloadAsync(null!, null, None));
        Assert.Empty(worker.Posted);
    }

    [Fact]
    public async Task The_local_manifest_is_read_and_validated()
    {
        var worker = new SimulatedStorageWorkerChannel();
        worker.LocalManifests["alpine-test/1.0.0"] = Manifest;
        worker.LocalManifests["alpine-test/2.0.0"] = Manifest;
        worker.LocalManifests["alpine-test/3.0.0"] = TestManifests.With("format", 7);
        await using var store = new ImageStore(worker);

        ImageManifest manifest = await store.ReadManifestAsync("alpine-test", "1.0.0", None);
        var mismatch = await Assert.ThrowsAsync<StorageException>(async () => await store.ReadManifestAsync("alpine-test", "2.0.0", None));
        var unsupported = await Assert.ThrowsAsync<StorageException>(async () => await store.ReadManifestAsync("alpine-test", "3.0.0", None));
        var missing = await Assert.ThrowsAsync<StorageException>(async () => await store.ReadManifestAsync("alpine-test", "4.0.0", None));

        Assert.Equal("alpine-test", manifest.Image);
        Assert.Equal(StorageErrorCodes.ManifestInvalid, mismatch.Code);
        Assert.Equal(StorageErrorCodes.FormatUnsupported, unsupported.Code);
        Assert.Equal(StorageErrorCodes.NotFound, missing.Code);
    }

    [Fact]
    public async Task Verify_rehashes_the_stored_image_and_reports_the_bad_chunks()
    {
        var worker = new SimulatedStorageWorkerChannel();
        worker.LocalManifests["alpine-test/1.0.0"] = Manifest;
        worker.BadChunks.Add(2);
        await using var store = new ImageStore(worker);
        var progress = new Progress();

        VerificationResult result = await store.VerifyAsync("alpine-test", "1.0.0", progress, None);

        Assert.Equal(4, result.ChunksTotal);
        Assert.Equal([2], result.BadChunks);
        Assert.False(result.IsSound);
        Assert.Single(progress.Reports);
        Assert.Equal(["read-manifest", "verify"], worker.PostedTypes);
    }

    [Fact]
    public void Locate_gives_the_payload_path_and_the_range_of_a_file()
    {
        ImageManifest manifest = ImageManifest.Parse(Manifest);

        ImageFileLocation kernel = ImageStore.Locate(manifest, "kernel");
        var missing = Assert.Throws<StorageException>(() => ImageStore.Locate(manifest, "rootfs"));

        Assert.Equal(new ImageFileLocation("osx86/images/alpine-test/1.0.0/image.bin", 0, 4096), kernel);
        Assert.Equal(StorageErrorCodes.NotFound, missing.Code);
        Assert.Throws<ArgumentNullException>(() => ImageStore.Locate(null!, "kernel"));
        Assert.Throws<ArgumentException>(() => ImageStore.Locate(manifest, ""));
    }

    [Fact]
    public async Task A_channel_that_cannot_post_fails_the_request_with_worker_error()
    {
        var worker = new SimulatedStorageWorkerChannel { PostException = new InvalidOperationException("JS gone") };
        await using var store = new ImageStore(worker);

        var error = await Assert.ThrowsAsync<StorageException>(async () => await store.ListAsync(None));

        Assert.Equal(StorageErrorCodes.WorkerError, error.Code);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task A_channel_that_cannot_start_fails_every_operation()
    {
        var worker = new SimulatedStorageWorkerChannel { StartException = new InvalidOperationException("no JS") };
        await using var store = new ImageStore(worker);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ListAsync(None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.EstimateAsync(None));
    }

    [Fact]
    public async Task Disposing_fails_the_pending_requests_and_refuses_new_ones()
    {
        var worker = new SimulatedStorageWorkerChannel { Silent = true };
        var store = new ImageStore(worker);
        await worker.SendAsync(new { type = "ready", opfs = true, locks = true });
        ValueTask<IReadOnlyList<StoredImage>> list = store.ListAsync(None);

        await store.DisposeAsync();
        await store.DisposeAsync();

        var error = await Assert.ThrowsAsync<StorageException>(async () => await list);
        Assert.Equal(StorageErrorCodes.Disposed, error.Code);
        Assert.True(worker.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.ListAsync(None));
    }

    [Fact]
    public async Task Disposing_before_ready_fails_the_waiting_operation()
    {
        var worker = new SimulatedStorageWorkerChannel { SendReady = false, Silent = true };
        var store = new ImageStore(worker);
        ValueTask<IReadOnlyList<StoredImage>> list = store.ListAsync(None);

        await store.DisposeAsync();

        var error = await Assert.ThrowsAsync<StorageException>(async () => await list);
        Assert.Equal(StorageErrorCodes.Disposed, error.Code);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_ready_throws()
    {
        var worker = new SimulatedStorageWorkerChannel { SendReady = false, Silent = true };
        await using var store = new ImageStore(worker);
        using var cancellation = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.ListAsync(cancellation.Token));
    }

    [Fact]
    public async Task Cancelling_a_plain_request_drops_it_without_telling_the_worker()
    {
        var worker = new SimulatedStorageWorkerChannel { Silent = true };
        await using var store = new ImageStore(worker);
        await worker.SendAsync(new { type = "ready", opfs = true, locks = true });
        using var cancellation = new CancellationTokenSource();

        ValueTask<IReadOnlyList<StoredImage>> list = store.ListAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await list);
        Assert.Equal(["list"], worker.PostedTypes);
        await worker.SendAsync(new { type = "images", id = 1, images = new List<object>() }); // late answer: ignored
    }

    [Fact]
    public void A_null_channel_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ImageStore(null!));
    }

    /// <summary>A clock the tests control, for the metadata timestamp and the cancel grace period.</summary>
    private sealed class FakeTime : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        private readonly ManualTimers _timers = new();
        private DateTimeOffset _now = Now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            _now += by;
            _timers.Fire(_now);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _timers.Add(callback, state, _now + dueTime);

        private sealed class ManualTimers
        {
            private readonly List<ManualTimer> _timers = [];

            public ITimer Add(TimerCallback callback, object? state, DateTimeOffset due)
            {
                var timer = new ManualTimer(callback, state, due);
                lock (_timers)
                {
                    _timers.Add(timer);
                }

                return timer;
            }

            public void Fire(DateTimeOffset now)
            {
                ManualTimer[] due;
                lock (_timers)
                {
                    due = _timers.Where(t => t.Due <= now).ToArray();
                    _timers.RemoveAll(t => t.Due <= now);
                }

                foreach (ManualTimer timer in due)
                {
                    timer.Fire();
                }
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
