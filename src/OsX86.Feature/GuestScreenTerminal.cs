using System.Globalization;
using System.Text;
using Kazo.Os.AppSdk;
using OsX86.Dos;
using OsX86.Engine;

namespace OsX86.Feature;

/// <summary>What a terminal can do with a guest text screen (decision 215 A).</summary>
/// <param name="CellColors">Whether each cell keeps its colours.</param>
/// <param name="FunctionKeys">Whether F1 to F12 reach the program (otherwise the Escape chord gives them).</param>
/// <param name="AltKey">Whether Alt+key reaches the program (otherwise the Escape chord gives it).</param>
/// <param name="PartialRows">Whether only the changed rows are sent (otherwise every frame is whole).</param>
internal sealed record GuestTerminalCapabilities(bool CellColors, bool FunctionKeys, bool AltKey, bool PartialRows);

/// <summary>What the terminal gave: keys, or a change of its size (the screen is then shown whole again).</summary>
/// <param name="Keys">The keys typed, in order (a paste gives several).</param>
/// <param name="Resized">Whether the terminal changed size.</param>
internal readonly record struct GuestTerminalInput(IReadOnlyList<GuestKey> Keys, bool Resized);

/// <summary>
/// The terminal surface a guest text screen is shown on (work item 11). This is the boundary that
/// ADR 0252 of Kazo (colours and function keys of a guest program in the Terminal) will enrich: the
/// DOS bridge only speaks to it, and today's adapter, <see cref="KazoFullScreenTerminal"/>, maps it on
/// the <see cref="IFullScreenTerminalSession"/> of the Kazo 3.4.0 SDK.
/// </summary>
internal interface IGuestScreenTerminal
{
    /// <summary>What this terminal can do.</summary>
    GuestTerminalCapabilities Capabilities { get; }

    /// <summary>Takes the screen and the keys (an alternate screen).</summary>
    ValueTask EnterAsync(CancellationToken cancellationToken);

    /// <summary>Shows <paramref name="screen"/>; <paramref name="changedRows"/> are the rows that differ from the last one shown.</summary>
    ValueTask ShowAsync(EngineTextScreen screen, IReadOnlyList<int> changedRows, CancellationToken cancellationToken);

    /// <summary>Waits for the next keys or size change.</summary>
    ValueTask<GuestTerminalInput> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Gives the screen and the keys back.</summary>
    ValueTask LeaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The 16 colours of the VGA text modes, as SGR sequences of 24-bit colour (the terminal's own theme
/// never changes how a DOS program looks).
/// </summary>
internal static class VgaColors
{
    /// <summary>The default palette of the VGA BIOS, index 0 to 15, as RGB.</summary>
    public static readonly int[] Palette =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    ];

    /// <summary>The foreground and background colour indexes of an attribute.</summary>
    public static (int Foreground, int Background) Split(byte attribute, bool blinkEnabled) =>
        (attribute & 0x0F, blinkEnabled ? (attribute >> 4) & 0x07 : attribute >> 4);

    /// <summary>The SGR sequence that sets the colours of an attribute.</summary>
    public static string Sgr(byte attribute, bool blinkEnabled)
    {
        (int foreground, int background) = Split(attribute, blinkEnabled);
        int fg = Palette[foreground];
        int bg = Palette[background];
        return string.Create(CultureInfo.InvariantCulture, $"\u001b[38;2;{fg >> 16};{(fg >> 8) & 0xFF};{fg & 0xFF};48;2;{bg >> 16};{(bg >> 8) & 0xFF};{bg & 0xFF}m");
    }

    /// <summary>A row of the screen with an SGR sequence before each run of cells of the same attribute.</summary>
    public static string FormatRow(EngineTextScreen screen, int row)
    {
        string line = row < screen.Lines.Count ? screen.Lines[row] : string.Empty;
        var builder = new StringBuilder(line.Length * 2);
        int? current = null;
        for (int column = 0; column < line.Length; column++)
        {
            byte attribute = screen.AttributeAt(row, column);
            if (current != attribute)
            {
                builder.Append(Sgr(attribute, screen.BlinkEnabled));
                current = attribute;
            }

            char c = line[column];
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// <see cref="IGuestScreenTerminal"/> on the full-screen session of the Kazo 3.4.0 SDK (work item 11,
/// until ADR 0252): every frame is whole (the SDK replaces the frame), each row carries its colours
/// as SGR sequences the way <c>htop</c> colours its rows, rows are formatted again only when they
/// changed. When the terminal is smaller than the guest screen, the rows shown follow the cursor (the
/// top rows, or the bottom ones once the cursor is below them) and the columns past its width are cut.
/// Keys: Kazo's Terminal decodes the Meta prefix as Alt, Tab and Ctrl+letter as characters; it has no
/// function keys (the Escape chord of <see cref="EscapeChord"/> gives them).
/// </summary>
internal sealed class KazoFullScreenTerminal(IFullScreenTerminalSession session, string label) : IGuestScreenTerminal
{
    private string[] _rows = [];

    /// <inheritdoc />
    public GuestTerminalCapabilities Capabilities { get; } = new(CellColors: true, FunctionKeys: false, AltKey: true, PartialRows: false);

    /// <inheritdoc />
    public ValueTask EnterAsync(CancellationToken cancellationToken) => session.EnterAlternateScreenAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask LeaveAsync(CancellationToken cancellationToken) => session.LeaveAlternateScreenAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask ShowAsync(EngineTextScreen screen, IReadOnlyList<int> changedRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(changedRows);
        return session.RenderAsync(Frame(screen, changedRows, session.Viewport), cancellationToken);
    }

    /// <summary>The frame of <paramref name="screen"/> for a terminal of <paramref name="viewport"/>.</summary>
    internal TerminalScreenFrame Frame(EngineTextScreen screen, IReadOnlyList<int> changedRows, TerminalViewport viewport)
    {
        if (_rows.Length != screen.Lines.Count)
        {
            _rows = new string[screen.Lines.Count];
            changedRows = [.. Enumerable.Range(0, screen.Lines.Count)];
        }

        foreach (int row in changedRows)
        {
            if (row >= 0 && row < _rows.Length)
            {
                _rows[row] = VgaColors.FormatRow(screen, row);
            }
        }

        int shown = Math.Max(1, Math.Min(viewport.Rows, _rows.Length));
        EngineTextCursor cursor = screen.Cursor ?? new EngineTextCursor(0, 0, false);
        int top = cursor.Row >= shown ? _rows.Length - shown : 0;
        string[] lines = [.. _rows.Skip(top).Take(shown).Select(static row => row ?? string.Empty)];
        int cursorRow = Math.Clamp(cursor.Row - top, 0, shown - 1);
        bool visible = cursor.Visible && cursor.Row - top >= 0 && cursor.Row - top < shown && cursor.Column < viewport.Columns;
        return new TerminalScreenFrame(lines, new TerminalCursor(cursor.Column, cursorRow, visible), label);
    }

    /// <inheritdoc />
    public async ValueTask<GuestTerminalInput> ReadAsync(CancellationToken cancellationToken)
    {
        TerminalKeyEvent key = await session.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        return key.Key == TerminalKey.Resize ? new GuestTerminalInput([], Resized: true) : new GuestTerminalInput(ToGuestKeys(key), Resized: false);
    }

    /// <summary>The guest keys of a key of Kazo's Terminal (several for a pasted text, none for what a PC keyboard cannot type).</summary>
    internal static IReadOnlyList<GuestKey> ToGuestKeys(TerminalKeyEvent key)
    {
        ArgumentNullException.ThrowIfNull(key);
        GuestKeyCode? code = key.Key switch
        {
            TerminalKey.Enter => GuestKeyCode.Enter,
            TerminalKey.Backspace => GuestKeyCode.Backspace,
            TerminalKey.Delete => GuestKeyCode.Delete,
            TerminalKey.ArrowLeft => GuestKeyCode.Left,
            TerminalKey.ArrowRight => GuestKeyCode.Right,
            TerminalKey.ArrowUp => GuestKeyCode.Up,
            TerminalKey.ArrowDown => GuestKeyCode.Down,
            TerminalKey.Home => GuestKeyCode.Home,
            TerminalKey.End => GuestKeyCode.End,
            TerminalKey.PageUp => GuestKeyCode.PageUp,
            TerminalKey.PageDown => GuestKeyCode.PageDown,
            TerminalKey.Escape => GuestKeyCode.Escape,
            _ => null,
        };
        if (code is { } special)
        {
            return [new GuestKey(special, Control: key.Control, Alt: key.Alt, Shift: key.Shift)];
        }

        if (key.Key != TerminalKey.Character || string.IsNullOrEmpty(key.Text))
        {
            return [];
        }

        var keys = new List<GuestKey>(key.Text.Length);
        foreach (char c in key.Text)
        {
            if (c == '\t')
            {
                keys.Add(new GuestKey(GuestKeyCode.Tab, Shift: key.Shift));
            }
            else if (GuestKeyboard.HasKey(c))
            {
                keys.Add(GuestKey.Of(c, key.Control, key.Alt));
            }
        }

        return keys;
    }
}
