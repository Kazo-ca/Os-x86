using Microsoft.Playwright;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Engine.V86;

namespace OsX86.Tests.Agent;

/// <summary>
/// Puts the C# agent client on the guest of the test page (<c>Guest/guest.html</c>): the page
/// hands every chunk of the virtio-console ports 0 and 1 to this process (a Playwright binding)
/// and writes what the client sends. The bytes go through the real <see cref="V86Console"/>
/// (line framing, readers) and <see cref="EngineAgentChannel"/>, so the client runs exactly as it
/// does on an engine; only the worker channel is replaced by the page.
/// </summary>
internal sealed class GuestAgentBridge
{
    private const string BindingName = "osx86AgentBytes";

    private readonly IPage _page;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, (string Channel, string Data)> _waiting = [];
    private readonly V86Console _protocolPort;
    private readonly V86Console _consolePort;
    private long _nextSequence;

    private GuestAgentBridge(IPage page)
    {
        _page = page;
        _protocolPort = new V86Console(EngineAgentChannel.ProtocolPort, WriteAsync);
        _consolePort = new V86Console(EngineAgentChannel.ConsolePort, WriteAsync);
        Channel = new EngineAgentChannel(_protocolPort, _consolePort);
    }

    /// <summary>The channel to give to an <see cref="AgentClient"/>.</summary>
    public EngineAgentChannel Channel { get; }

    /// <summary>
    /// Installs the binding and the tap; call it once the page has <c>window.guest</c> and before
    /// the guest boots, so that nothing the agent writes is missed.
    /// </summary>
    public static async Task<GuestAgentBridge> AttachAsync(IPage page)
    {
        var bridge = new GuestAgentBridge(page);
        await page.ExposeFunctionAsync<long, string, string, bool>(BindingName, bridge.OnChunk);
        await page.EvaluateAsync(
            "() => { let sequence = 0; window.guest.consoleTap = (channel, data) => { if (channel === 'virtio0' || channel === 'virtio1') window."
            + BindingName + "(sequence++, channel, data); }; }");
        return bridge;
    }

    /// <summary>Ends the readers of both ports, as a disposed engine does.</summary>
    public void Complete()
    {
        _protocolPort.Complete();
        _consolePort.Complete();
    }

    /// <summary>
    /// A chunk from the page. Binding calls may be dispatched out of order, so the page numbers
    /// the chunks it forwards (without gaps) and they are delivered here in that order.
    /// </summary>
    private bool OnChunk(long sequence, string channel, string data)
    {
        lock (_lock)
        {
            _waiting[sequence] = (channel, data);
            while (_waiting.Remove(_nextSequence, out (string Channel, string Data) chunk))
            {
                _nextSequence++;
                (chunk.Channel == "virtio0" ? _protocolPort : _consolePort).Deliver(Convert.FromBase64String(chunk.Data));
            }
        }

        return true;
    }

    private async ValueTask WriteAsync(ConsoleId port, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.EvaluateAsync("([channel, data]) => window.guest.sendBase64(channel, data)", new[] { port.ChannelName, Convert.ToBase64String(data.Span) });
    }
}
