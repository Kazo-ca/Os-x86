namespace OsX86.Engine;

/// <summary>
/// One console port of a running engine, read as a raw byte stream or line by line, and written
/// to. Readers are independent: each call to a read method sees everything the guest writes from
/// that moment on. Writes are only allowed while the engine is starting, running or paused.
/// </summary>
public interface IEngineConsole
{
    /// <summary>Which port this is.</summary>
    ConsoleId Id { get; }

    /// <summary>The bytes the guest writes, as they arrive (chunks of the worker protocol).</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(CancellationToken cancellationToken);

    /// <summary>The lines the guest writes (UTF-8, terminated by LF, the trailing CR removed).</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);

    /// <summary>Sends bytes to the guest.</summary>
    /// <exception cref="EngineStateException">The engine is stopped or stopping.</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Sends a line to the guest: <paramref name="text"/> encoded in UTF-8 followed by LF.</summary>
    /// <exception cref="EngineStateException">The engine is stopped or stopping.</exception>
    ValueTask WriteLineAsync(string text, CancellationToken cancellationToken);
}
