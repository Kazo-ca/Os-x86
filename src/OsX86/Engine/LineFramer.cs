using System.Text;

namespace OsX86.Engine;

/// <summary>
/// Turns the byte chunks of a console port into lines: UTF-8 decoded across chunk boundaries,
/// one line per LF, the trailing CR removed (the guest's tty writes CR LF). A line longer than
/// <see cref="MaxLineLength"/> is cut there, so a guest that never writes LF cannot grow the buffer
/// without bound. <see cref="Pending"/> is what arrived after the last LF: a prompt, typically.
/// </summary>
public sealed class LineFramer
{
    /// <summary>The default cut-off for a line without LF.</summary>
    public const int DefaultMaxLineLength = 64 * 1024;

    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
    private readonly StringBuilder _pending = new();

    /// <summary>Creates a framer that cuts lines at <paramref name="maxLineLength"/> characters.</summary>
    public LineFramer(int maxLineLength = DefaultMaxLineLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        MaxLineLength = maxLineLength;
    }

    /// <summary>The cut-off for a line without LF.</summary>
    public int MaxLineLength { get; }

    /// <summary>The characters received after the last LF.</summary>
    public string Pending => _pending.ToString();

    /// <summary>Feeds bytes and returns the lines they complete, in order (possibly none).</summary>
    public IReadOnlyList<string> Push(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }

        char[] chars = new char[_decoder.GetCharCount(bytes, flush: false)];
        int count = _decoder.GetChars(bytes, chars, flush: false);
        List<string>? lines = null;
        for (int index = 0; index < count; index++)
        {
            char c = chars[index];
            if (c == '\n')
            {
                (lines ??= []).Add(TakeLine());
            }
            else
            {
                _pending.Append(c);
                if (_pending.Length >= MaxLineLength)
                {
                    (lines ??= []).Add(TakeLine());
                }
            }
        }

        return lines ?? (IReadOnlyList<string>)[];
    }

    /// <summary>Returns the pending characters as a final line and clears them; <see langword="null"/> when there are none.</summary>
    public string? Flush()
    {
        if (_pending.Length == 0)
        {
            return null;
        }

        return TakeLine();
    }

    private string TakeLine()
    {
        int length = _pending.Length;
        if (length > 0 && _pending[length - 1] == '\r')
        {
            length--;
        }

        string line = _pending.ToString(0, length);
        _pending.Clear();
        return line;
    }
}
