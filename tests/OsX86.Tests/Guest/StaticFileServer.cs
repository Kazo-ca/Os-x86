using System.Net;
using System.Net.Sockets;

namespace OsX86.Tests.Guest;

/// <summary>
/// A small HTTP server for the guest tests: serves a few directories of the repository under
/// fixed URL prefixes (the engine assets, the image, the test page) on a free localhost port.
/// </summary>
internal sealed class StaticFileServer : IAsyncDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".wasm"] = "application/wasm",
        [".css"] = "text/css",
    };

    private readonly HttpListener _listener = new();
    private readonly IReadOnlyList<(string Prefix, string Directory)> _routes;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Creates the server; <paramref name="routes"/> maps URL prefixes (<c>/engine/</c>) to directories.</summary>
    public StaticFileServer(IReadOnlyList<(string Prefix, string Directory)> routes)
    {
        _routes = routes.Select(r => (r.Prefix.EndsWith('/') ? r.Prefix : r.Prefix + "/", Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Directory)) + Path.DirectorySeparatorChar)).ToList();
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    public int Port { get; }

    public string BaseUrl => $"http://localhost:{Port}/";

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
            string? file = Resolve(path);
            if (context.Request.HttpMethod != "GET" || file is null)
            {
                response.StatusCode = file is null ? 404 : 405;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            response.Headers["Cache-Control"] = "no-store";
            await using FileStream stream = File.OpenRead(file);
            response.ContentLength64 = stream.Length;
            await stream.CopyToAsync(response.OutputStream).ConfigureAwait(false);
            response.Close();
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
