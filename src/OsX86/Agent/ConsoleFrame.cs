using System.Buffers.Binary;

namespace OsX86.Agent;

/// <summary>The kinds of frames on the console port.</summary>
public enum ConsoleFrameType
{
    /// <summary>Bytes of a console: input when sent to the guest, output when received.</summary>
    Data = 1,

    /// <summary>Guest to host only, after the last <see cref="Data"/> of a console: its exit code and signal.</summary>
    Closed = 2,
}

/// <summary>
/// One frame of the console port (<c>hvc1</c>), on which the bytes of every user console travel
/// (decision 213 A, correction 3 of 205 A):
/// <c>0xAE type console(4, big endian) length(2, big endian) payload</c>. The console number is the
/// sequence number of the console's launch. The agent side is <c>images/agent/src/console.c</c>.
/// </summary>
/// <param name="Type">What the frame carries; a value the reader does not know is kept as is and ignored by the client.</param>
/// <param name="Console">The console number.</param>
/// <param name="Payload">The bytes.</param>
public readonly record struct ConsoleFrame(ConsoleFrameType Type, int Console, ReadOnlyMemory<byte> Payload)
{
    /// <summary>The first byte of every frame.</summary>
    public const byte Magic = 0xAE;

    /// <summary>The length of the header.</summary>
    public const int HeaderLength = 8;

    /// <summary>The largest payload the client sends in one frame (the agent does the same).</summary>
    public const int MaxPayloadLength = 4096;

    /// <summary>The largest console number a frame can carry.</summary>
    public const int MaxConsole = int.MaxValue;

    /// <summary>
    /// Encodes <paramref name="data"/> as <see cref="ConsoleFrameType.Data"/> frames for
    /// <paramref name="console"/>, one buffer holding as many frames as the data needs.
    /// </summary>
    public static byte[] EncodeData(int console, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(console);
        int frames = (data.Length + MaxPayloadLength - 1) / MaxPayloadLength;
        byte[] buffer = new byte[data.Length + frames * HeaderLength];
        int offset = 0;
        while (!data.IsEmpty)
        {
            int take = Math.Min(data.Length, MaxPayloadLength);
            buffer[offset] = Magic;
            buffer[offset + 1] = (byte)ConsoleFrameType.Data;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset + 2), console);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 6), (ushort)take);
            data[..take].CopyTo(buffer.AsSpan(offset + HeaderLength));
            offset += HeaderLength + take;
            data = data[take..];
        }

        return buffer;
    }

    /// <summary>Reads the payload of a <see cref="ConsoleFrameType.Closed"/> frame: exit code and signal, two big-endian int32.</summary>
    public bool TryReadExit(out ProcessExit exit)
    {
        exit = default;
        if (Type != ConsoleFrameType.Closed || Payload.Length < 8)
        {
            return false;
        }

        exit = new ProcessExit(BinaryPrimitives.ReadInt32BigEndian(Payload.Span), BinaryPrimitives.ReadInt32BigEndian(Payload.Span[4..]));
        return true;
    }
}

/// <summary>
/// Turns the chunks of the console port into frames. A frame may arrive in any number of chunks
/// and a chunk may hold several frames; a byte that is not <see cref="ConsoleFrame.Magic"/> where a
/// frame should start is skipped (counted in <see cref="SkippedBytes"/>), which resynchronises a
/// reader that joined the stream in the middle of a frame.
/// </summary>
public sealed class ConsoleFrameReader
{
    private readonly byte[] _header = new byte[ConsoleFrame.HeaderLength];
    private int _headerLength;
    private byte[]? _payload;
    private int _payloadLength;

    /// <summary>How many bytes were skipped while looking for the start of a frame.</summary>
    public long SkippedBytes { get; private set; }

    /// <summary>Feeds bytes and returns the frames they complete, in order (possibly none).</summary>
    public IReadOnlyList<ConsoleFrame> Push(ReadOnlySpan<byte> bytes)
    {
        List<ConsoleFrame>? frames = null;
        while (!bytes.IsEmpty)
        {
            if (_payload is not null)
            {
                int take = Math.Min(bytes.Length, _payload.Length - _payloadLength);
                bytes[..take].CopyTo(_payload.AsSpan(_payloadLength));
                _payloadLength += take;
                bytes = bytes[take..];
                if (_payloadLength == _payload.Length)
                {
                    (frames ??= []).Add(Complete());
                }

                continue;
            }

            if (_headerLength == 0 && bytes[0] != ConsoleFrame.Magic)
            {
                SkippedBytes++;
                bytes = bytes[1..];
                continue;
            }

            _header[_headerLength++] = bytes[0];
            bytes = bytes[1..];
            if (_headerLength == ConsoleFrame.HeaderLength)
            {
                _payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(6))];
                _payloadLength = 0;
                if (_payload.Length == 0)
                {
                    (frames ??= []).Add(Complete());
                }
            }
        }

        return frames ?? (IReadOnlyList<ConsoleFrame>)[];
    }

    private ConsoleFrame Complete()
    {
        var frame = new ConsoleFrame((ConsoleFrameType)_header[1], BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(2)), _payload!);
        _payload = null;
        _headerLength = 0;
        return frame;
    }
}
