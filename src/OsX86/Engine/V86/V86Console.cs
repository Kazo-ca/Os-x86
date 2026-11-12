using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace OsX86.Engine.V86;

/// <summary>
/// A console port of <see cref="V86Engine"/>: fans the bytes of the worker out to every raw
/// reader, frames them into lines for every line reader, and sends writes through the engine.
/// A reader is registered as soon as <see cref="ReadRawAsync"/> or <see cref="ReadLinesAsync"/>
/// is called (not at the first enumeration), keeps a bounded queue (the oldest items are dropped
/// when a reader does not keep up), ends with its token, and completes when the engine is
/// disposed. The chunks given to raw readers are shared: read them, do not write them.
/// </summary>
internal sealed class V86Console : IEngineConsole
{
    /// <summary>Chunks or lines a reader may have waiting before the oldest are dropped.</summary>
    public const int ReaderQueueCapacity = 4096;

    private readonly Func<ConsoleId, ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private readonly LineFramer _framer = new();
    private readonly List<Channel<ReadOnlyMemory<byte>>> _rawReaders = [];
    private readonly List<Channel<string>> _lineReaders = [];
    private readonly Lock _lock = new();
    private bool _completed;

    /// <summary>Creates the port; <paramref name="write"/> is the engine's way to reach the guest.</summary>
    public V86Console(ConsoleId id, Func<ConsoleId, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write)
    {
        Id = id;
        _write = write;
    }

    /// <inheritdoc />
    public ConsoleId Id { get; }

    /// <summary>What the guest wrote after its last line feed (a prompt, typically).</summary>
    public string PendingText
    {
        get
        {
            lock (_lock)
            {
                return _framer.Pending;
            }
        }
    }

    /// <summary>Called by the engine for every chunk the worker posts for this port.</summary>
    public void Deliver(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            foreach (Channel<ReadOnlyMemory<byte>> reader in _rawReaders)
            {
                reader.Writer.TryWrite(bytes);
            }

            foreach (string line in _framer.Push(bytes.Span))
            {
                PublishLineUnsafe(line);
            }
        }
    }

    /// <summary>Turns the pending partial line into a line for the line readers (when the engine stops).</summary>
    public void FlushPendingLine()
    {
        lock (_lock)
        {
            if (_framer.Flush() is { } line)
            {
                PublishLineUnsafe(line);
            }
        }
    }

    /// <summary>Ends every reader; later deliveries are dropped (the engine is disposed).</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (Channel<ReadOnlyMemory<byte>> reader in _rawReaders)
            {
                reader.Writer.TryComplete();
            }

            foreach (Channel<string> reader in _lineReaders)
            {
                reader.Writer.TryComplete();
            }

            _rawReaders.Clear();
            _lineReaders.Clear();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(CancellationToken cancellationToken)
    {
        Channel<ReadOnlyMemory<byte>> channel = Register(_rawReaders);
        return Enumerate(channel, _rawReaders, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken)
    {
        Channel<string> channel = Register(_lineReaders);
        return Enumerate(channel, _lineReaders, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        data.IsEmpty ? ValueTask.CompletedTask : _write(Id, data, cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteLineAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _write(Id, Encoding.UTF8.GetBytes(text + "\n"), cancellationToken);
    }

    private void PublishLineUnsafe(string line)
    {
        foreach (Channel<string> reader in _lineReaders)
        {
            reader.Writer.TryWrite(line);
        }
    }

    private Channel<T> Register<T>(List<Channel<T>> readers)
    {
        Channel<T> channel = Channel.CreateBounded<T>(new BoundedChannelOptions(ReaderQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        lock (_lock)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                readers.Add(channel);
            }
        }

        return channel;
    }

    private async IAsyncEnumerable<T> Enumerate<T>(Channel<T> channel, List<Channel<T>> readers, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (T item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_lock)
            {
                readers.Remove(channel);
            }
        }
    }
}
