using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>
/// The HTTP server of the storage guest test, on the model of <c>Guest/StaticFileServer</c>:
/// serves a few directories of the repository under URL prefixes, and under <c>/image/</c> an
/// image generated on the fly (a deterministic xorshift32 pattern, never written to disk, never
/// committed) with its <c>manifest.json</c> in the schema of work item 3. Faults are injected
/// as in trial E3: 503 on the next GETs of a chunk, connections cut after N bytes on the next
/// chunk responses. Every chunk request is recorded.
/// </summary>
internal sealed class ChunkImageServer : IAsyncDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json",
    };

    private readonly HttpListener _listener = new();
    private readonly IReadOnlyList<(string Prefix, string Directory)> _routes;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<ChunkRequest> _requests = [];
    private Task? _loop;

    /// <summary>Creates the server for an image of <paramref name="sizeBytes"/> bytes in chunks of <paramref name="chunkSize"/>.</summary>
    public ChunkImageServer(IReadOnlyList<(string Prefix, string Directory)> routes, long sizeBytes, int chunkSize = ImageManifest.DefaultChunkSize, uint seed = 0x9e3779b9)
    {
        _routes = routes.Select(r => (r.Prefix.EndsWith('/') ? r.Prefix : r.Prefix + "/", Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Directory)) + Path.DirectorySeparatorChar)).ToList();
        Size = sizeBytes;
        ChunkSize = chunkSize;
        Seed = seed;
        ChunkCount = (int)((sizeBytes + chunkSize - 1) / chunkSize);
        ChunkHashes = Enumerable.Range(0, ChunkCount).AsParallel().AsOrdered().Select(index => Convert.ToHexStringLower(SHA256.HashData(ChunkBytes(index)))).ToList();
        ManifestJson = BuildManifest();
        ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ManifestJson)));
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    /// <summary>One chunk request as the server saw it.</summary>
    /// <param name="Index">The chunk index.</param>
    /// <param name="Status">The HTTP status answered.</param>
    /// <param name="BytesSent">How many bytes of the body went out before the end or the cut.</param>
    /// <param name="Cut">Whether the connection was cut on purpose.</param>
    public sealed record ChunkRequest(int Index, int Status, long BytesSent, bool Cut);

    public long Size { get; }

    public int ChunkSize { get; }

    public int ChunkCount { get; }

    public uint Seed { get; }

    public IReadOnlyList<string> ChunkHashes { get; }

    public string ManifestJson { get; }

    public string ManifestSha256 { get; }

    public int Port { get; }

    public string BaseUrl => $"http://localhost:{Port}/";

    public string ImageUrl => BaseUrl + "image/";

    /// <summary>Answer 503 to this many chunk GETs before serving normally.</summary>
    public int ServiceUnavailableNext { get; set; }

    /// <summary>Cut the connection on this many chunk responses after <see cref="CutAfterBytes"/> bytes.</summary>
    public int CutNextResponses { get; set; }

    /// <summary>How many bytes go out before a cut.</summary>
    public long CutAfterBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Only the chunk responses at or after this ordinal (1-based, counted over all chunk GETs) are cut: 0 = any.</summary>
    public int CutFromResponse { get; set; }

    /// <summary>Every chunk request so far, in order.</summary>
    public IReadOnlyList<ChunkRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }

    public void ClearRequests()
    {
        lock (_lock)
        {
            _requests.Clear();
        }
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The listener was stopped under the loop: expected.
            }
        }

        _listener.Close();
        _stop.Dispose();
    }

    /// <summary>The bytes of chunk <paramref name="index"/>: xorshift32 seeded by the image seed and the index.</summary>
    public byte[] ChunkBytes(int index)
    {
        long length = Math.Min(ChunkSize, Size - (long)index * ChunkSize);
        var bytes = new byte[length];
        uint x = Seed ^ (uint)(index + 1) * 0x85ebca6bu;
        int offset = 0;
        while (offset + 4 <= bytes.Length)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), x);
            offset += 4;
        }

        for (; offset < bytes.Length; offset++)
        {
            bytes[offset] = (byte)(x >> (8 * (offset & 3)));
        }

        return bytes;
    }

    private string BuildManifest()
    {
        var manifest = new
        {
            format = 1,
            image = "storage-test",
            version = "1.0.0",
            engine = new { v86 = "0.5.473" },
            size = Size,
            chunkSize = ChunkSize,
            chunks = ChunkHashes.Select(sha256 => new { sha256 }).ToList(),
            files = new Dictionary<string, object> { ["rootfs"] = new { offset = 0, size = Size } },
        };
        return JsonSerializer.Serialize(manifest);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            response.Headers["Cache-Control"] = "no-store";
            if (context.Request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            if (path == "/image/manifest.json")
            {
                await WriteAsync(response, Encoding.UTF8.GetBytes(ManifestJson), "application/json").ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/image/chunk-", StringComparison.Ordinal) && path.EndsWith(".bin", StringComparison.Ordinal)
                && int.TryParse(path["/image/chunk-".Length..^4], out int index) && index >= 0 && index < ChunkCount)
            {
                await ServeChunkAsync(response, index).ConfigureAwait(false);
                return;
            }

            string? file = Resolve(path);
            if (file is null)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            await WriteAsync(response, await File.ReadAllBytesAsync(file).ConfigureAwait(false), ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream")).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                response.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, byte[] bytes, string contentType)
    {
        response.StatusCode = 200;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private async Task ServeChunkAsync(HttpListenerResponse response, int index)
    {
        bool unavailable;
        bool cut;
        lock (_lock)
        {
            unavailable = ServiceUnavailableNext > 0;
            if (unavailable)
            {
                ServiceUnavailableNext--;
            }

            cut = !unavailable && CutNextResponses > 0 && _requests.Count + 1 >= CutFromResponse;
            if (cut)
            {
                CutNextResponses--;
            }
        }

        if (unavailable)
        {
            response.StatusCode = 503;
            response.Close();
            Record(new ChunkRequest(index, 503, 0, false));
            return;
        }

        byte[] bytes = ChunkBytes(index);
        response.StatusCode = 200;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = bytes.Length;
        long sent = 0;
        const int Block = 256 * 1024;
        try
        {
            while (sent < bytes.Length)
            {
                if (cut && sent >= CutAfterBytes)
                {
                    response.Abort();
                    Record(new ChunkRequest(index, 200, sent, true));
                    return;
                }

                int count = (int)Math.Min(Block, bytes.Length - sent);
                await response.OutputStream.WriteAsync(bytes.AsMemory((int)sent, count)).ConfigureAwait(false);
                sent += count;
            }

            response.Close();
            Record(new ChunkRequest(index, 200, sent, false));
        }
        catch (Exception)
        {
            Record(new ChunkRequest(index, 200, sent, false)); // the client went away (cancel, reload)
            throw;
        }
    }

    private void Record(ChunkRequest request)
    {
        lock (_lock)
        {
            _requests.Add(request);
        }
    }

    private string? Resolve(string path)
    {
        foreach ((string prefix, string directory) in _routes)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = Uri.UnescapeDataString(path[prefix.Length..]);
            if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }

            string full = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(full) ? full : null;
        }

        return null;
    }
}
