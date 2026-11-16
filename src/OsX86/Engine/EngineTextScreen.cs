namespace OsX86.Engine;

/// <summary>
/// The VGA text screen of the guest (trial E14: how a DOS program is read; work item 11: with the
/// colours and the cursor, decisions 214 A and 218 A). In a graphical mode it keeps the last text.
/// </summary>
/// <param name="Columns">The width in characters.</param>
/// <param name="Rows">The height in characters.</param>
/// <param name="Lines">One string per row, code page 437 turned into Unicode, padded with spaces.</param>
/// <param name="Attributes">
/// One VGA attribute byte per cell, row by row (<c>Rows × Columns</c>): bits 0-3 the foreground,
/// 4-6 the background, 7 blink or bright background (<paramref name="BlinkEnabled"/>);
/// <see langword="null"/> when the engine only knows the characters (light grey on black then).
/// </param>
/// <param name="Cursor">The hardware cursor, <see langword="null"/> when the engine cannot read it.</param>
/// <param name="BlinkEnabled">Whether bit 7 of an attribute blinks instead of brightening the background.</param>
/// <param name="Graphical">Whether the VGA was in a graphical mode when read (the text is then frozen).</param>
public sealed record EngineTextScreen(
    int Columns,
    int Rows,
    IReadOnlyList<string> Lines,
    byte[]? Attributes = null,
    EngineTextCursor? Cursor = null,
    bool BlinkEnabled = false,
    bool Graphical = false)
{
    /// <summary>The attribute DOS writes by default: light grey on black.</summary>
    public const byte DefaultAttribute = 0x07;

    /// <summary>The rows joined by line feeds, trailing spaces removed.</summary>
    public string Text => string.Join('\n', Lines.Select(static line => line.TrimEnd()));

    /// <summary>The attribute of a cell; <see cref="DefaultAttribute"/> outside the screen or without attributes.</summary>
    public byte AttributeAt(int row, int column)
    {
        int index = (row * Columns) + column;
        return Attributes is { } attributes && row >= 0 && column >= 0 && column < Columns && index < attributes.Length ? attributes[index] : DefaultAttribute;
    }
}

/// <summary>The hardware cursor of the VGA text screen.</summary>
/// <param name="Row">Its row, from 0.</param>
/// <param name="Column">Its column, from 0.</param>
/// <param name="Visible">Whether the guest shows it.</param>
public readonly record struct EngineTextCursor(int Row, int Column, bool Visible);

/// <summary>
/// An engine whose guest can be driven like a PC in text mode (work item 11, the DOS way of 214 A):
/// its VGA text screen read and its PS/2 keyboard fed with scancodes. The v86 engine is one.
/// </summary>
public interface ITextModeEngine
{
    /// <summary>The VGA text screen as it is now, with its attributes and cursor when the engine can read them.</summary>
    ValueTask<EngineTextScreen> ReadTextScreenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends PS/2 scancodes (set 1: make codes, break = make | 0x80, extended keys after 0xE0) to the
    /// guest keyboard, in order. Only when the engine was started with <see cref="EngineStartOptions.Keyboard"/>.
    /// </summary>
    /// <exception cref="EngineStateException">The engine is not running.</exception>
    ValueTask SendScancodesAsync(IReadOnlyList<byte> scancodes, CancellationToken cancellationToken);
}
