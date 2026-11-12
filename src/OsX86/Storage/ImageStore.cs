namespace OsX86.Storage;

/// <summary>
/// The image repository in the origin private file system (plan section 1, decisions 203 A and
/// 177 A): downloads images through the storage worker (fetch, SHA-256 per chunk, write with a
/// sync access handle, resume state), lists, verifies and deletes them, and reports the storage
/// estimate. The C# side owns the decisions: it validates the manifest before anything is
/// written (decision 169) and computes the <see cref="ResumePlan"/>; the worker executes.
/// One operation per request; several requests may be in flight (a list during a download).
/// </summary>
public sealed class ImageStore : IAsyncDisposable
{
    private static readonly TimeSpan CancelAcknowledgeTimeout = TimeSpan.FromSeconds(5);

    private readonly IStorageWorkerChannel _channel;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PendingRequest> _pending = [];
    private readonly TaskCompletionSource<StorageMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _channelStart;
    private StorageException? _faulted;
    private int _nextId;
    private bool _disposed;

    /// <summary>Creates a store on a worker channel; the worker starts with the first operation.</summary>
    public ImageStore(IStorageWorkerChannel channel, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _time = time ?? TimeProvider.System;
        _ = _ready.Task.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously); // observed: a failed start is reported through the operations
    }

    /// <summary>Raised for an error the worker reports outside any request (a crash, a message that is not the protocol).</summary>
    public event EventHandler<StorageException>? UnroutedErrorReported;

    /// <summary>Whether the worker reported the OPFS available (null until the worker is ready).</summary>
    public bool? OpfsAvailable { get; private set; }

    /// <summary>Whether the worker reported the Web Locks API available (null until the worker is ready).</summary>
    public bool? WebLocksAvailable { get; private set; }

    /// <summary>Where a logical file of an image is in the OPFS: the payload path and the range in it.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.NotFound"/> when the manifest has no such file.</exception>
    public static ImageFileLocation Locate(ImageManifest manifest, string file)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(file);
        if (!manifest.Files.TryGetValue(file, out ImageFileEntry? entry))
        {
            throw new StorageException(StorageErrorCodes.NotFound, $"The image {manifest.Image} {manifest.Version} has no file '{file}'.");
        }

        return new ImageFileLocation(StorageLayout.PayloadPath(manifest.Image, manifest.Version), entry.Offset, entry.Size);
    }

    /// <summary>The image versions present in the repository, complete or not.</summary>
    public async ValueTask<IReadOnlyList<StoredImage>> ListAsync(CancellationToken cancellationToken)
    {
        int id = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        StorageMessage answer = await RequestAsync(StorageProtocol.List(id), id, StorageProtocol.Types.Images, null, cancellationToken).ConfigureAwait(false);
        return (answer.Images ?? []).Select(ToStoredImage).ToList();
    }

    /// <summary>What the browser reports about the storage of the origin, plus what the repository occupies.</summary>
    public async ValueTask<StorageEstimate> EstimateAsync(CancellationToken cancellationToken)
    {
        int id = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        StorageMessage answer = await RequestAsync(StorageProtocol.Estimate(id), id, StorageProtocol.Types.Estimate, null, cancellationToken).ConfigureAwait(false);
        return new StorageEstimate(answer.Usage, answer.Quota, answer.RepositoryBytes);
    }

    /// <summary>Deletes one image version, whatever its state; nothing when it does not exist.</summary>
    /// <exception cref="StorageException"><see cref="StorageErrorCodes.Locked"/> while a download writes it.</exception>
    public async ValueTask DeleteAsync(string image, string version, CancellationToken cancellationToken)
    {
        StorageLayout.ImageDirectory(image, version);
        int id = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        await RequestAsync(StorageProtocol.Delete(id, image, version), id, StorageProtocol.Types.Done, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the local copy of the manifest of a stored image version, validated (an unknown format is refused, decision 169).</summary>
    public async ValueTask<ImageManifest> ReadManifestAsync(string image, string version, CancellationToken cancellationToken)
    {
        StorageLayout.ImageDirectory(image, version);
        int id = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        StorageMessage answer = await RequestAsync(StorageProtocol.ReadManifest(id, image, version), id, StorageProtocol.Types.Manifest, null, cancellationToken).ConfigureAwait(false);
        ImageManifest manifest = ImageManifest.Parse(answer.Text ?? string.Empty);
        if (manifest.Image != image || manifest.Version != version)
        {
            throw new StorageException(StorageErrorCodes.ManifestInvalid, $"The stored manifest of {image} {version} describes {manifest.Image} {manifest.Version}.");
        }

        return manifest;
    }

    /// <summary>
    /// Downloads an image: fetches the manifest and checks its hash, validates it, reads the
    /// resume state, then has the worker fetch, verify and write the missing chunks. Nothing is
    /// written before the manifest is accepted. Cancelling keeps the verified chunks for a later resume.
    /// </summary>
    /// <exception cref="StorageException">With the code that names what failed.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async ValueTask<StoredImage> DownloadAsync(ImageSource source, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        var current = new DownloadProgress(DownloadPhase.Idle, 0, 0, 0, 0, 0);
        void Report(DownloadProgress next)
        {
            current = next;
            progress?.Report(next);
        }

        try
        {
            int id = await PrepareAsync(cancellationToken).ConfigureAwait(false);
            Report(new DownloadProgress(DownloadPhase.FetchingManifest, 0, 0, 0, 0, 0));
            StorageMessage fetched = await RequestAsync(StorageProtocol.FetchManifest(id, source.ManifestUrl, source.ManifestSha256.ToLowerInvariant()), id, StorageProtocol.Types.Manifest, null, cancellationToken).ConfigureAwait(false);
            ImageManifest manifest = ImageManifest.Parse(System.Text.Encoding.UTF8.GetBytes(fetched.Text ?? string.Empty), source.ManifestSha256);

            Report(new DownloadProgress(DownloadPhase.Planning, 0, manifest.Size, 0, manifest.ChunkCount, 0));
            int stateId = NextId();
            StorageMessage stored = await RequestAsync(StorageProtocol.ReadState(stateId, manifest.Image, manifest.Version), stateId, StorageProtocol.Types.State, null, cancellationToken).ConfigureAwait(false);
            ResumePlan plan = ResumePlan.Create(manifest, stored.State);

            int downloadId = NextId();
            var command = new DownloadCommand
            {
                Id = downloadId,
                Image = manifest.Image,
                Version = manifest.Version,
                BaseUrl = source.BaseUrl,
                Manifest = manifest.Json,
                Metadata = StoredObjectMetadata.ForImage(manifest, _time.GetUtcNow()).ToJson(),
                Chunks = plan.ChunksToFetch,
                Reset = plan.Reset,
                Parallelism = source.Parallelism,
            };
            Report(new DownloadProgress(DownloadPhase.Downloading, plan.BytesVerified, manifest.Size, plan.ChunksVerified.Count, manifest.ChunkCount, 0));
            StorageMessage done = await RequestAsync(StorageProtocol.Download(command), downloadId, StorageProtocol.Types.Done, message => Report(ToProgress(DownloadPhase.Downloading, message)), cancellationToken, cancellable: true).ConfigureAwait(false);
            Report(new DownloadProgress(DownloadPhase.Completed, manifest.Size, manifest.Size, manifest.ChunkCount, manifest.ChunkCount, 0));
            return new StoredImage(manifest.Image, manifest.Version, manifest.Size, done.BytesOnDisk, true, StoredObjectMetadata.CurrentFormat, manifest.Engine);
        }
        catch (OperationCanceledException)
        {
            Report(current with { Phase = DownloadPhase.Cancelled, BytesPerSecond = 0 });
            throw;
        }
        catch (Exception exception) when (exception is StorageException or ObjectDisposedException)
        {
            Report(current with { Phase = DownloadPhase.Failed, BytesPerSecond = 0 });
            throw;
        }
    }

    /// <summary>Re-reads and hashes every chunk of a stored image against its local manifest.</summary>
    public async ValueTask<VerificationResult> VerifyAsync(string image, string version, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ImageManifest manifest = await ReadManifestAsync(image, version, cancellationToken).ConfigureAwait(false);
        int id = NextId();
        var command = new VerifyCommand { Id = id, Image = image, Version = version, Manifest = manifest.Json };
        StorageMessage answer = await RequestAsync(StorageProtocol.Verify(command), id, StorageProtocol.Types.Verified, message => progress?.Report(ToProgress(DownloadPhase.Downloading, message)), cancellationToken, cancellable: true).ConfigureAwait(false);
        return new VerificationResult(answer.ChunksTotal, answer.Bad ?? []);
    }

    /// <summary>Ends the store: the worker is terminated and every pending request fails with <see cref="StorageErrorCodes.Disposed"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        List<PendingRequest> pending;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending.Values.ToList();
            _pending.Clear();
        }

        var disposed = new StorageException(StorageErrorCodes.Disposed, "The image store was disposed.");
        foreach (PendingRequest request in pending)
        {
            request.Completion.TrySetException(disposed);
        }

        _ready.TrySetException(disposed);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private static StoredImage ToStoredImage(StoredImageEntry entry) =>
        new(entry.Image, entry.Version, entry.Size, entry.BytesOnDisk, entry.Complete, entry.Format, entry.Engine ?? new Dictionary<string, string>());

    private static DownloadProgress ToProgress(DownloadPhase phase, StorageMessage message) =>
        new(phase, message.BytesVerified, message.BytesTotal, message.ChunksVerified, message.ChunksTotal, message.BytesPerSecond);

    private int NextId()
    {
        lock (_lock)
        {
            return ++_nextId;
        }
    }

    /// <summary>Starts the worker once, waits for its <c>ready</c>, checks the OPFS, and gives the id of the next request.</summary>
    private async ValueTask<int> PrepareAsync(CancellationToken cancellationToken)
    {
        Task start;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            start = _channelStart ??= _channel.StartAsync(OnWorkerMessageAsync, CancellationToken.None).AsTask();
        }

        try
        {
            await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (start.IsFaulted)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_channelStart, start))
                {
                    _channelStart = null; // a failed start is forgotten: the next operation tries again
                }
            }

            throw;
        }
        StorageMessage ready = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!ready.Opfs)
        {
            throw new StorageException(StorageErrorCodes.OpfsUnavailable, "The origin private file system with sync access handles is not available in the storage worker.");
        }

        return NextId();
    }

    private void ThrowIfFaulted()
    {
        if (_faulted is not null)
        {
            throw new StorageException(_faulted.Code, $"The storage worker is down: {_faulted.Message}", _faulted);
        }
    }

    private async ValueTask<StorageMessage> RequestAsync(string json, int id, string expectedType, Action<StorageMessage>? onProgress, CancellationToken cancellationToken, bool cancellable = false)
    {
        var request = new PendingRequest(expectedType, onProgress);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            _pending.Add(id, request);
        }

        // Posted without the token: once dispatched, JS runs the command whatever the token does, so the
        // cancellation is observed on the wait below and answered with a cancel command when it matters.
        try
        {
            await _channel.PostAsync(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            Remove(id);
            throw;
        }
        catch (Exception exception)
        {
            Remove(id);
            if (_disposed)
            {
                throw new StorageException(StorageErrorCodes.Disposed, "The image store was disposed.", exception);
            }

            throw new StorageException(StorageErrorCodes.WorkerError, $"The storage worker could not be reached: {exception.Message}", exception);
        }

        try
        {
            return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelRequestAsync(id, request, cancellable).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>On cancellation: tells the worker to stop a cancellable request and waits a little for its acknowledgement, so the lock on the image is released before the caller goes on.</summary>
    private async ValueTask CancelRequestAsync(int id, PendingRequest request, bool cancellable)
    {
        if (cancellable && !request.Completion.Task.IsCompleted)
        {
            try
            {
                await _channel.PostAsync(StorageProtocol.Cancel(NextId(), id), CancellationToken.None).ConfigureAwait(false);
                await request.Completion.Task.WaitAsync(CancelAcknowledgeTimeout, _time).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The worker is gone or slow to acknowledge: the caller's cancellation wins either way.
            }
        }

        Remove(id);
    }

    private void Remove(int id)
    {
        lock (_lock)
        {
            _pending.Remove(id);
        }
    }

    private ValueTask OnWorkerMessageAsync(string json)
    {
        StorageMessage message;
        try
        {
            message = StorageProtocol.Parse(json);
        }
        catch (StorageException exception)
        {
            UnroutedErrorReported?.Invoke(this, exception);
            return ValueTask.CompletedTask;
        }

        if (message.Type == StorageProtocol.Types.Ready)
        {
            OpfsAvailable = message.Opfs;
            WebLocksAvailable = message.Locks;
            _ready.TrySetResult(message);
            return ValueTask.CompletedTask;
        }

        if (message.Id is not int id)
        {
            OnUnroutedMessage(message);
            return ValueTask.CompletedTask;
        }

        PendingRequest? request;
        lock (_lock)
        {
            _pending.TryGetValue(id, out request);
        }

        if (request is null)
        {
            return ValueTask.CompletedTask; // a late answer to a cancelled or unknown request
        }

        Route(id, request, message);
        return ValueTask.CompletedTask;
    }

    private void Route(int id, PendingRequest request, StorageMessage message)
    {
        if (message.Type == StorageProtocol.Types.Progress)
        {
            try
            {
                request.OnProgress?.Invoke(message);
            }
            catch (Exception exception)
            {
                UnroutedErrorReported?.Invoke(this, new StorageException(StorageErrorCodes.WorkerError, $"A progress callback threw: {exception.Message}", exception)); // the download goes on
            }

            return;
        }

        Remove(id);
        if (message.Type == StorageProtocol.Types.Error)
        {
            string code = string.IsNullOrEmpty(message.Code) ? StorageErrorCodes.WorkerError : message.Code;
            string text = message.Message ?? "the storage worker reported an error";
            if (code == StorageErrorCodes.Cancelled)
            {
                request.Completion.TrySetCanceled();
            }
            else
            {
                request.Completion.TrySetException(new StorageException(code, text));
            }

            return;
        }

        if (message.Type != request.ExpectedType)
        {
            request.Completion.TrySetException(new StorageException(StorageErrorCodes.Protocol, $"The storage worker answered '{message.Type}' where '{request.ExpectedType}' was expected."));
            return;
        }

        request.Completion.TrySetResult(message);
    }

    /// <summary>An error without a request id: the worker itself failed. Before <c>ready</c>, that is the start failing; after, every pending request fails.</summary>
    private void OnUnroutedMessage(StorageMessage message)
    {
        if (message.Type != StorageProtocol.Types.Error)
        {
            return;
        }

        var exception = new StorageException(string.IsNullOrEmpty(message.Code) ? StorageErrorCodes.WorkerError : message.Code, message.Message ?? "the storage worker failed");
        if (_ready.TrySetException(exception))
        {
            return;
        }

        List<PendingRequest> pending;
        lock (_lock)
        {
            _faulted ??= exception; // the worker is dead: later requests fail at once instead of hanging
            pending = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (PendingRequest request in pending)
        {
            request.Completion.TrySetException(exception);
        }

        UnroutedErrorReported?.Invoke(this, exception);
    }

    private sealed class PendingRequest(string expectedType, Action<StorageMessage>? onProgress)
    {
        public string ExpectedType { get; } = expectedType;

        public Action<StorageMessage>? OnProgress { get; } = onProgress;

        public TaskCompletionSource<StorageMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
