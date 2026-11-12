using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>
/// An <see cref="IStorageWorkerChannel"/> that plays the storage worker: records every command,
/// answers like <c>storage-worker.js</c> would (manifest hash check, resume state, progress,
/// completion), and can be told to fail, to hang or to stay silent so the tests drive the store
/// through every path. Same role as <c>SimulatedWorkerChannel</c> for the engine.
/// </summary>
internal sealed class SimulatedStorageWorkerChannel : IStorageWorkerChannel
{
    private Func<string, ValueTask>? _onMessage;
    private readonly Dictionary<int, string> _hanging = [];

    public List<string> Posted { get; } = [];

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>When set, the worker does not answer commands; tests send the messages themselves.</summary>
    public bool Silent { get; set; }

    /// <summary>Whether a <c>ready</c> is posted at start.</summary>
    public bool SendReady { get; set; } = true;

    public bool ReadyOpfs { get; set; } = true;

    public bool ReadyLocks { get; set; } = true;

    /// <summary>What <c>fetch-manifest</c> returns; null answers a fetch failure.</summary>
    public string? ManifestText { get; set; }

    /// <summary>The resume state answered to <c>read-state</c>, per "image/version".</summary>
    public Dictionary<string, DownloadStateSnapshot> States { get; } = [];

    /// <summary>The local manifests answered to <c>read-manifest</c>, per "image/version".</summary>
    public Dictionary<string, string> LocalManifests { get; } = [];

    /// <summary>What <c>list</c> returns.</summary>
    public List<object> Images { get; } = [];

    /// <summary>When set, a download fails with this error after one progress message.</summary>
    public (string Code, string Message)? DownloadError { get; set; }

    /// <summary>When set, downloads (and verifications) never finish until cancelled.</summary>
    public bool HangDownloads { get; set; }

    /// <summary>When set, a cancel is never acknowledged.</summary>
    public bool IgnoreCancel { get; set; }

    /// <summary>The chunks <c>verify</c> reports as bad.</summary>
    public List<int> BadChunks { get; } = [];

    /// <summary>When set, <see cref="StartAsync"/> throws.</summary>
    public Exception? StartException { get; set; }

    /// <summary>When set, <see cref="PostAsync"/> throws before recording anything.</summary>
    public Exception? PostException { get; set; }

    /// <summary>How long every <see cref="PostAsync"/> takes before the command is recorded (JS interop latency).</summary>
    public TimeSpan PostDelay { get; set; }

    /// <summary>When set, cancelled as soon as a download command is posted, before the post completes.</summary>
    public CancellationTokenSource? CancelOnDownloadPost { get; set; }

    /// <summary>The last download command received, parsed.</summary>
    public JsonElement? LastDownload { get; private set; }

    public async ValueTask StartAsync(Func<string, ValueTask> onMessage, CancellationToken cancellationToken)
    {
        if (StartException is not null)
        {
            throw StartException;
        }

        Started = true;
        _onMessage = onMessage;
        if (SendReady)
        {
            await SendAsync(new { type = "ready", opfs = ReadyOpfs, locks = ReadyLocks });
        }
    }

    public async ValueTask PostAsync(string json, CancellationToken cancellationToken)
    {
        if (PostException is not null)
        {
            throw PostException;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement.Clone();
        string type = root.GetProperty("type").GetString()!;
        int id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetInt32() : 0;
        if (type == "download")
        {
            CancelOnDownloadPost?.Cancel();
        }

        if (PostDelay > TimeSpan.Zero)
        {
            await Task.Delay(PostDelay, CancellationToken.None);
        }

        Posted.Add(json);
        if (Silent)
        {
            return;
        }

        switch (type)
        {
            case "fetch-manifest":
                await AnswerFetchManifestAsync(id, root.GetProperty("sha256").GetString()!);
                break;
            case "read-state":
                States.TryGetValue(Key(root), out DownloadStateSnapshot? state);
                await SendAsync(new { type = "state", id, state = state is null ? null : new { manifestSha256 = state.ManifestSha256, verified = state.Verified, complete = state.Complete } });
                break;
            case "download":
                LastDownload = root;
                await AnswerDownloadAsync(id, root);
                break;
            case "cancel":
                await AnswerCancelAsync(root.GetProperty("target").GetInt32());
                break;
            case "list":
                await SendAsync(new { type = "images", id, images = Images, repositoryBytes = 1234 });
                break;
            case "delete":
                States.Remove(Key(root));
                await SendAsync(new { type = "done", id });
                break;
            case "estimate":
                await SendAsync(new { type = "estimate", id, usage = 5000, quota = 10_000_000, repositoryBytes = 1234 });
                break;
            case "read-manifest":
                await AnswerReadManifestAsync(id, Key(root));
                break;
            case "verify":
                await AnswerVerifyAsync(id, root);
                break;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a raw message to the store, as the worker would.</summary>
    public ValueTask SendRawAsync(string json) => _onMessage?.Invoke(json) ?? ValueTask.CompletedTask;

    /// <summary>Sends an object as JSON to the store.</summary>
    public ValueTask SendAsync(object message) => SendRawAsync(JsonSerializer.Serialize(message));

    /// <summary>The type of every command posted so far.</summary>
    public IReadOnlyList<string> PostedTypes => Posted
        .Select(json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!)
        .ToList();

    /// <summary>The SHA-256 of a text, as the worker computes it.</summary>
    public static string Sha256Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Key(JsonElement root) => $"{root.GetProperty("image").GetString()}/{root.GetProperty("version").GetString()}";

    private async Task AnswerFetchManifestAsync(int id, string expected)
    {
        if (ManifestText is null)
        {
            await SendAsync(new { type = "error", id, code = "manifest-fetch-failed", message = "HTTP 404" });
            return;
        }

        string actual = Sha256Of(ManifestText);
        if (actual != expected)
        {
            await SendAsync(new { type = "error", id, code = "manifest-integrity", message = $"the manifest hashes to {actual}, not to the expected {expected}" });
            return;
        }

        await SendAsync(new { type = "manifest", id, text = ManifestText });
    }

    private async Task AnswerDownloadAsync(int id, JsonElement command)
    {
        using JsonDocument manifest = JsonDocument.Parse(command.GetProperty("manifest").GetString()!);
        long size = manifest.RootElement.GetProperty("size").GetInt64();
        int chunks = manifest.RootElement.GetProperty("chunks").GetArrayLength();
        await SendAsync(new { type = "progress", id, bytesVerified = size / 2, bytesTotal = size, chunksVerified = chunks / 2, chunksTotal = chunks, bytesPerSecond = 1_000_000 });
        if (DownloadError is { } failure)
        {
            await SendAsync(new { type = "error", id, code = failure.Code, message = failure.Message });
            return;
        }

        if (HangDownloads)
        {
            _hanging[id] = "download";
            return;
        }

        States[Key(command)] = new DownloadStateSnapshot(Sha256Of(command.GetProperty("manifest").GetString()!), Enumerable.Range(0, chunks).ToList(), true);
        await SendAsync(new { type = "done", id, bytesOnDisk = size + 500 });
    }

    private async Task AnswerCancelAsync(int target)
    {
        if (IgnoreCancel || !_hanging.Remove(target))
        {
            return;
        }

        await SendAsync(new { type = "error", id = target, code = "cancelled", message = "the download was cancelled" });
    }

    private async Task AnswerReadManifestAsync(int id, string key)
    {
        if (LocalManifests.TryGetValue(key, out string? text))
        {
            await SendAsync(new { type = "manifest", id, text });
        }
        else
        {
            await SendAsync(new { type = "error", id, code = "not-found", message = $"{key}: no manifest in the repository" });
        }
    }

    private async Task AnswerVerifyAsync(int id, JsonElement command)
    {
        using JsonDocument manifest = JsonDocument.Parse(command.GetProperty("manifest").GetString()!);
        int chunks = manifest.RootElement.GetProperty("chunks").GetArrayLength();
        await SendAsync(new { type = "progress", id, bytesVerified = 0, bytesTotal = manifest.RootElement.GetProperty("size").GetInt64(), chunksVerified = 0, chunksTotal = chunks, bytesPerSecond = 0 });
        if (HangDownloads)
        {
            _hanging[id] = "verify";
            return;
        }

        await SendAsync(new { type = "verified", id, chunksTotal = chunks, bad = BadChunks });
    }
}
