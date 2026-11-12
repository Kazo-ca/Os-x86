using System.Text.Json;
using System.Threading.Channels;
using OsX86.Agent;

namespace OsX86.Tests.Agent;

/// <summary>
/// An <see cref="IAgentChannel"/> that plays the guest agent: records every request, answers with
/// what the test registered for the operation (the <c>id</c> is put in for it), and lets the test
/// send any line or console byte itself, stay silent, or fail, so that the client can be driven
/// through every path without a guest.
/// </summary>
internal sealed class SimulatedAgentChannel : IAgentChannel
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Channel<ReadOnlyMemory<byte>> _consoleBytes = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly Dictionary<string, Func<JsonElement, IEnumerable<string>>> _handlers = [];
    private readonly Lock _lock = new();
    private readonly List<string> _sent = [];
    private readonly List<byte[]> _consoleWritten = [];

    public SimulatedAgentChannel()
    {
        On("ping", _ => """{"ok":true,"version":1,"uptime":12,"rssKb":180,"launches":0,"consoles":0,"cgroups":true}""");
    }

    /// <summary>Requests left unanswered before the handlers apply (a guest that is still booting).</summary>
    public int IgnoreFirstRequests { get; set; }

    /// <summary>When set, no request is answered.</summary>
    public bool Silent { get; set; }

    /// <summary>When set, <see cref="WriteLineAsync"/> throws it.</summary>
    public Exception? WriteException { get; set; }

    /// <summary>Every request line the client wrote.</summary>
    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>Every buffer the client wrote on the console port.</summary>
    public IReadOnlyList<byte[]> ConsoleWritten
    {
        get
        {
            lock (_lock)
            {
                return [.. _consoleWritten];
            }
        }
    }

    /// <summary>The requests of one operation, parsed.</summary>
    public IReadOnlyList<JsonElement> Requests(string operation) =>
        Sent.Select(line => JsonDocument.Parse(line).RootElement).Where(r => r.GetProperty("op").GetString() == operation).ToList();

    /// <summary>The single request of one operation.</summary>
    public JsonElement Request(string operation) => Assert.Single(Requests(operation));

    /// <summary>Answers an operation with one reply: a JSON object without <c>id</c>.</summary>
    public void On(string operation, Func<JsonElement, string> reply) => _handlers[operation] = request => [WithId(request, reply(request))];

    /// <summary>Answers an operation with a reply followed by other lines (events), sent in one go.</summary>
    public void On(string operation, Func<JsonElement, string> reply, params string[] thenLines) =>
        _handlers[operation] = request => [WithId(request, reply(request)), .. thenLines];

    /// <summary>Leaves an operation unanswered.</summary>
    public void Ignore(string operation) => _handlers[operation] = _ => [];

    /// <summary>Sends a line to the client, as the agent would.</summary>
    public void SendLine(string line) => _lines.Writer.TryWrite(line);

    /// <summary>Sends the reply to the request numbered <paramref name="id"/>.</summary>
    public void Reply(long id, string body) => SendLine("{\"id\":" + id + "," + body.TrimStart().TrimStart('{'));

    /// <summary>Sends bytes on the console port, as the agent would.</summary>
    public void SendConsoleBytes(ReadOnlyMemory<byte> bytes) => _consoleBytes.Writer.TryWrite(bytes);

    /// <summary>Ends both streams, as a disposed engine does.</summary>
    public void Complete()
    {
        _lines.Writer.TryComplete();
        _consoleBytes.Writer.TryComplete();
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) => _lines.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadConsoleBytesAsync(CancellationToken cancellationToken) => _consoleBytes.Reader.ReadAllAsync(cancellationToken);

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteException is not null)
        {
            throw WriteException;
        }

        int index;
        lock (_lock)
        {
            _sent.Add(line);
            index = _sent.Count;
        }

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement request = document.RootElement.Clone();
        if (Silent || index <= IgnoreFirstRequests || !_handlers.TryGetValue(request.GetProperty("op").GetString()!, out var handler))
        {
            return ValueTask.CompletedTask;
        }

        foreach (string reply in handler(request))
        {
            SendLine(reply);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteConsoleBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _consoleWritten.Add(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    private static string WithId(JsonElement request, string body) =>
        "{\"id\":" + request.GetProperty("id").GetInt64() + "," + body.TrimStart().TrimStart('{');
}
