using System.Text;

namespace OsX86.Feature;

/// <summary>
/// Turns the bytes of a guest pseudo-terminal into text for the line-mode terminal of Kazo, until
/// ADR 0252 gives the Terminal a VT emulator: UTF-8 decoded across chunks, carriage returns and
/// escape sequences (CSI, OSC, two-byte ESC) dropped, other control characters dropped except
/// LF, tab and BEL. A cursor-forward sequence (CSI n C) becomes n spaces: the console of Wine draws
/// the blanks of a line that way (work item 10: <c>a b</c> came out as <c>ab</c>). The pty echoes what is typed; the Kazo terminal already shows the typed line,
/// so the echo of each line the command sent is removed once, when it comes back as is.
/// </summary>
internal sealed class TerminalTextFilter
{
    private const char Escape = '\u001b';

    /// <summary>How many sent lines may wait for their echo at once.</summary>
    private const int MaxPending = 8;

    /// <summary>The most spaces one cursor-forward sequence gives (a terminal line, at most).</summary>
    private const int MaxForward = 1000;
    private readonly StringBuilder _csi = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly Queue<string> _expectedEcho = new();
    private readonly StringBuilder _echoMatched = new();
    private EscapeState _escape;
    private int _echoPosition;
    private int _forward;

    private enum EscapeState
    {
        None,
        Escape,
        Csi,
        Osc,
        OscEscape,
    }

    /// <summary>Expects the echo of a line the command sent (without its LF; the filter adds it).</summary>
    public void ExpectEcho(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0)
        {
            return;
        }

        if (_expectedEcho.Count == MaxPending)
        {
            _expectedEcho.Dequeue();
            _echoMatched.Clear();
            _echoPosition = 0;
        }

        _expectedEcho.Enqueue(line + "\n");
    }

    /// <summary>The text of a chunk of output.</summary>
    public string Push(ReadOnlySpan<byte> bytes)
    {
        char[] chars = new char[_decoder.GetCharCount(bytes, flush: false)];
        int count = _decoder.GetChars(bytes, chars, flush: false);
        var output = new StringBuilder(count);
        foreach (char c in chars.AsSpan(0, count))
        {
            if (Visible(c) is { } visible)
            {
                Emit(visible, output);
            }
            else if (_forward > 0)
            {
                for (; _forward > 0; _forward--)
                {
                    Emit(' ', output);
                }
            }
        }

        return output.ToString();
    }

    /// <summary>What is left at the end of the stream: an echo that never completed is shown.</summary>
    public string Flush()
    {
        string pending = _echoMatched.ToString();
        _echoMatched.Clear();
        _echoPosition = 0;
        return pending;
    }

    private char? Visible(char c)
    {
        switch (_escape)
        {
            case EscapeState.Escape:
                _csi.Clear();
                _escape = c switch
                {
                    '[' => EscapeState.Csi,
                    ']' => EscapeState.Osc,
                    _ => EscapeState.None, // a two-byte sequence (ESC 7, ESC =, ...)
                };
                return null;
            case EscapeState.Csi:
                if (c is >= '@' and <= '~')
                {
                    _escape = EscapeState.None; // the final byte
                    if (c == 'C')
                    {
                        _forward = ForwardCount(_csi.ToString());
                    }
                }
                else if (_csi.Length < 32)
                {
                    _csi.Append(c);
                }

                return null;
            case EscapeState.Osc:
                _escape = c switch
                {
                    '\u0007' => EscapeState.None,
                    Escape => EscapeState.OscEscape,
                    _ => EscapeState.Osc,
                };
                return null;
            case EscapeState.OscEscape:
                _escape = c == '\\' ? EscapeState.None : EscapeState.Osc;
                return null;
        }

        if (c == Escape)
        {
            _escape = EscapeState.Escape;
            return null;
        }

        if (c is '\n' or '\t' or '\u0007')
        {
            return c;
        }

        return char.IsControl(c) ? null : c;
    }

    /// <summary>The count of a cursor-forward sequence: its first parameter, 1 when absent or zero.</summary>
    private static int ForwardCount(string parameters)
    {
        string first = parameters.Split(';')[0];
        return int.TryParse(first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0
            ? Math.Min(n, MaxForward)
            : 1;
    }

    private void Emit(char c, StringBuilder output)
    {
        if (_expectedEcho.Count == 0)
        {
            output.Append(c);
            return;
        }

        string expected = _expectedEcho.Peek();
        if (expected[_echoPosition] == c)
        {
            _echoMatched.Append(c);
            if (++_echoPosition == expected.Length)
            {
                _expectedEcho.Dequeue(); // the whole echo came back: it is not shown
                _echoMatched.Clear();
                _echoPosition = 0;
            }

            return;
        }

        // Not the echo: output of the program that came first (the echo of a line follows what the
        // guest printed before it). Show what was held and keep waiting for the echo; a line whose
        // echo never comes (a program that turned echo off) stops being expected after MaxPending lines.
        bool wasMatching = _echoPosition > 0;
        output.Append(_echoMatched);
        _echoMatched.Clear();
        _echoPosition = 0;
        if (wasMatching)
        {
            Emit(c, output);
            return;
        }

        output.Append(c);
    }
}
