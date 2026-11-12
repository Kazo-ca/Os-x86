using OsX86.Engine;

namespace OsX86.Agent;

/// <summary>
/// The two byte streams between the host and the guest agent: the protocol port (JSON, one
/// message per line; <c>hvc0</c> in the guest) and the console port (the frames of
/// <see cref="ConsoleFrame"/>; <c>hvc1</c>). <see cref="AgentClient"/> works on this abstraction;
/// <see cref="EngineAgentChannel"/> puts it on two console ports of an engine, the tests on a
/// simulation.
/// </summary>
public interface IAgentChannel
{
    /// <summary>The lines the agent writes on the protocol port, from the call on.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);

    /// <summary>Sends one line (the LF is added) to the agent on the protocol port.</summary>
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The bytes the agent writes on the console port, from the call on, in chunks of any size.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadConsoleBytesAsync(CancellationToken cancellationToken);

    /// <summary>Sends bytes to the agent on the console port.</summary>
    ValueTask WriteConsoleBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IAgentChannel"/> on two console ports of an engine: by default the virtio-console
/// ports 0 (<c>hvc0</c>, the protocol) and 1 (<c>hvc1</c>, the consoles), as the init of the
/// images starts the agent. The readers are registered when the read methods are called, so a
/// client that connects before the guest boots misses nothing.
/// </summary>
public sealed class EngineAgentChannel : IAgentChannel
{
    private readonly IEngineConsole _protocolPort;
    private readonly IEngineConsole _consolePort;

    /// <summary>Creates the channel on two ports.</summary>
    public EngineAgentChannel(IEngineConsole protocolPort, IEngineConsole consolePort)
    {
        ArgumentNullException.ThrowIfNull(protocolPort);
        ArgumentNullException.ThrowIfNull(consolePort);
        _protocolPort = protocolPort;
        _consolePort = consolePort;
    }

    /// <summary>The port of the protocol in the images of Os-x86: virtio-console 0.</summary>
    public static ConsoleId ProtocolPort { get; } = ConsoleId.Virtio(0);

    /// <summary>The port of the consoles in the images of Os-x86: virtio-console 1.</summary>
    public static ConsoleId ConsolePort { get; } = ConsoleId.Virtio(1);

    /// <summary>The channel on the default ports of <paramref name="engine"/>.</summary>
    public static EngineAgentChannel For(IEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new EngineAgentChannel(engine.GetConsole(ProtocolPort), engine.GetConsole(ConsolePort));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) => _protocolPort.ReadLinesAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken) => _protocolPort.WriteLineAsync(line, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadConsoleBytesAsync(CancellationToken cancellationToken) => _consolePort.ReadRawAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteConsoleBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => _consolePort.WriteAsync(data, cancellationToken);
}
