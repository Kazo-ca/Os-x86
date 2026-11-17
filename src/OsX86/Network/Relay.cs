using System.Threading.Channels;

namespace OsX86.Network;

/// <summary>What the gateway asks the relay: a TCP flow of an identity to a destination (ADR 0255 §1).</summary>
/// <param name="Identity">The identity (its <see cref="FlowIdentity.RelayLabel"/> is what the relay of Kazo gets as <c>identity=</c>).</param>
/// <param name="Host">The destination host.</param>
/// <param name="Port">The destination port.</param>
public sealed record RelayFlowRequest(FlowIdentity Identity, string Host, int Port);

/// <summary>An open flow through the relay: bytes both ways, then each side closes.</summary>
public interface IRelayStream : IAsyncDisposable
{
    /// <summary>Reads what the destination sent; 0 at its end.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Sends bytes to the destination.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>The guest finished sending (its FIN): the destination sees the end of its input.</summary>
    ValueTask CompleteWritesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The way out of the private network (174 A: the real network goes through the relay of Kazo,
/// which holds the decision, the limits and the journal as the source of truth). The relay of
/// Kazo that takes an identity per flow is ADR 0255, not coded yet: until then the Feature has
/// <see cref="UnavailableRelay"/>, and the tests and the dev host <see cref="LocalTestRelay"/>.
/// </summary>
public interface IRelayConnector
{
    /// <summary>Opens a flow.</summary>
    /// <exception cref="RelayException">The relay refused or could not reach the destination.</exception>
    ValueTask<IRelayStream> ConnectAsync(RelayFlowRequest request, CancellationToken cancellationToken);
}

/// <summary>A refusal or failure of the relay.</summary>
public sealed class RelayException : Exception
{
    /// <summary>The relay of Kazo refused the identity (ADR 0255: close code 4003).</summary>
    public const string IdentityDenied = "identity-denied";

    /// <summary>The relay of Kazo has no decision for the identity (ADR 0255: close code 4004).</summary>
    public const string IdentityUndecided = "identity-undecided";

    /// <summary>The destination did not answer or does not exist.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>No relay is available to this host.</summary>
    public const string Unavailable = "relay-unavailable";

    /// <summary>Creates the exception.</summary>
    public RelayException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates the exception (serialization and analyzers).</summary>
    public RelayException()
        : this(Unavailable, "The relay is not available.")
    {
    }

    /// <summary>Creates the exception (analyzers).</summary>
    public RelayException(string message)
        : this(Unavailable, message)
    {
    }

    /// <summary>Creates the exception (analyzers).</summary>
    public RelayException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = Unavailable;
    }

    /// <summary>What happened: one of the constants of this class.</summary>
    public string Code { get; }
}

/// <summary>
/// No relay: the case of the Feature until the relay of Kazo takes identities (ADR 0255). Every
/// allowed flow fails with <see cref="RelayException.Unavailable"/> and the journal says so.
/// </summary>
public sealed class UnavailableRelay : IRelayConnector
{
    /// <summary>The only instance.</summary>
    public static UnavailableRelay Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<IRelayStream> ConnectAsync(RelayFlowRequest request, CancellationToken cancellationToken) =>
        throw new RelayException(RelayException.Unavailable, "No relay takes the flows of Os-x86 yet: the relay of Kazo needs ADR 0255 (identities and decisions of a Feature).");
}

/// <summary>
/// A relay that never leaves the page: each destination it knows is a service answered in memory,
/// every other destination is unreachable. The tests and the dev host use it; it opens no socket
/// and no request, so a VM behind it has no real Internet. The default service of
/// <see cref="WithHttp"/> answers any GET with a short text, and <c>GET /blob?mb=N</c> (1 to 64) with
/// N MiB, for the throughput measures.
/// </summary>
public sealed class LocalTestRelay : IRelayConnector
{
    private readonly Dictionary<(string Host, int Port), Func<IRelayServiceConnection, CancellationToken, Task>> _services = [];
    private readonly Lock _lock = new();
    private readonly List<RelayFlowRequest> _requests = [];

    /// <summary>The flows asked for, in order (a test checks that a refused flow never got here).</summary>
    public IReadOnlyList<RelayFlowRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Adds a service at a destination.</summary>
    public LocalTestRelay With(string host, int port, Func<IRelayServiceConnection, CancellationToken, Task> service)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(service);
        lock (_lock)
        {
            _services[(host.ToLowerInvariant(), port)] = service;
        }

        return this;
    }

    /// <summary>Adds the test HTTP service at a destination.</summary>
    public LocalTestRelay WithHttp(string host, int port) => With(host, port, (connection, token) => TestHttpService.ServeAsync(connection, host, port, token));

    /// <inheritdoc />
    public ValueTask<IRelayStream> ConnectAsync(RelayFlowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Func<IRelayServiceConnection, CancellationToken, Task>? service;
        lock (_lock)
        {
            _requests.Add(request);
            _services.TryGetValue((request.Host.ToLowerInvariant(), request.Port), out service);
        }

        if (service is null)
        {
            throw new RelayException(RelayException.Unreachable, $"The test relay has no service at {request.Host}:{request.Port} (it never reaches the Internet).");
        }

        var pipe = new MemoryDuplex();
        _ = Task.Run(async () =>
        {
            try
            {
                await service(pipe.ServiceSide, pipe.Token).ConfigureAwait(false);
            }
            catch (Exception) when (pipe.Token.IsCancellationRequested)
            {
                // The flow was closed under the service.
            }
            finally
            {
                pipe.ServiceSide.Complete();
            }
        });
        return ValueTask.FromResult<IRelayStream>(pipe.GuestSide);
    }
}

/// <summary>The service side of a flow of <see cref="LocalTestRelay"/>.</summary>
public interface IRelayServiceConnection
{
    /// <summary>Reads what the guest sent; 0 when it finished sending.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Sends bytes to the guest.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>Two byte channels: what the guest writes the service reads, and the other way.</summary>
internal sealed class MemoryDuplex
{
    private readonly CancellationTokenSource _closed = new();

    public MemoryDuplex()
    {
        var toService = Channel.CreateBounded<ReadOnlyMemory<byte>>(64);
        var toGuest = Channel.CreateBounded<ReadOnlyMemory<byte>>(64);
        GuestSide = new End(toGuest.Reader, toService.Writer, _closed);
        ServiceSide = new End(toService.Reader, toGuest.Writer, _closed);
    }

    public End GuestSide { get; }

    public End ServiceSide { get; }

    public CancellationToken Token => _closed.Token;

    internal sealed class End(ChannelReader<ReadOnlyMemory<byte>> reader, ChannelWriter<ReadOnlyMemory<byte>> writer, CancellationTokenSource closed) : IRelayStream, IRelayServiceConnection
    {
        private ReadOnlyMemory<byte> _pending;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (_pending.IsEmpty)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                reader.TryRead(out _pending);
            }

            int count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
            {
                return;
            }

            await writer.WriteAsync(data.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
        {
            Complete();
            return ValueTask.CompletedTask;
        }

        public void Complete() => writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            writer.TryComplete();
            if (!closed.IsCancellationRequested)
            {
                closed.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The HTTP service of the test relay: one request, one answer, then the end of the flow.</summary>
internal static class TestHttpService
{
    public const int MaxRequestBytes = 8192;
    public const int MaxBlobMiB = 64;

    public static async Task ServeAsync(IRelayServiceConnection connection, string host, int port, CancellationToken cancellationToken)
    {
        string? request = await ReadRequestAsync(connection, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        string path = RequestPath(request);
        if (path.StartsWith("/blob?mb=", StringComparison.Ordinal) && int.TryParse(path["/blob?mb=".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int mib)
            && mib is >= 1 and <= MaxBlobMiB)
        {
            await WriteTextAsync(connection, $"HTTP/1.0 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {mib * 1048576L}\r\n\r\n", cancellationToken).ConfigureAwait(false);
            byte[] chunk = new byte[32768];
            for (int index = 0; index < chunk.Length; index++)
            {
                chunk[index] = (byte)index;
            }

            for (long sent = 0; sent < mib * 1048576L; sent += chunk.Length)
            {
                await connection.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string body = $"osx86-test-relay {host}:{port} {path}\n";
        await WriteTextAsync(connection, $"HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadRequestAsync(IRelayServiceConnection connection, CancellationToken cancellationToken)
    {
        var received = new List<byte>();
        byte[] buffer = new byte[1024];
        while (received.Count < MaxRequestBytes)
        {
            int count = await connection.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            received.AddRange(buffer.AsSpan(0, count));
            string text = System.Text.Encoding.ASCII.GetString([.. received]);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        return received.Count == 0 ? null : System.Text.Encoding.ASCII.GetString([.. received]);
    }

    private static string RequestPath(string request)
    {
        string[] words = request.Split([' ', '\r', '\n'], 3, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && words[1].StartsWith('/') ? words[1] : "/";
    }

    private static ValueTask WriteTextAsync(IRelayServiceConnection connection, string text, CancellationToken cancellationToken) =>
        connection.WriteAsync(System.Text.Encoding.ASCII.GetBytes(text), cancellationToken);
}
