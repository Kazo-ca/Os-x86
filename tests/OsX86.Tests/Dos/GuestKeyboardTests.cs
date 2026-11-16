using OsX86.Dos;
using OsX86.Engine;

namespace OsX86.Tests.Dos;

/// <summary>Keys to scancodes (<see cref="GuestKeyboard"/>), the Escape chord (<see cref="EscapeChord"/>) and the comparison by row (<see cref="TextScreenDiff"/>).</summary>
[Trait("Category", "unit")]
public sealed class GuestKeyboardTests
{
    private static byte[] Codes(GuestKey key) => [.. GuestKeyboard.Translate(key).SelectMany(static step => step.Scancodes)];

    [Theory]
    [InlineData('a', new byte[] { 0x1E, 0x9E })]
    [InlineData('z', new byte[] { 0x2C, 0xAC })]
    [InlineData('1', new byte[] { 0x02, 0x82 })]
    [InlineData('0', new byte[] { 0x0B, 0x8B })]
    [InlineData(' ', new byte[] { 0x39, 0xB9 })]
    [InlineData('\\', new byte[] { 0x2B, 0xAB })]
    [InlineData('A', new byte[] { 0x2A, 0x1E, 0x9E, 0xAA })]
    [InlineData('!', new byte[] { 0x2A, 0x02, 0x82, 0xAA })]
    [InlineData('?', new byte[] { 0x2A, 0x35, 0xB5, 0xAA })]
    [InlineData(':', new byte[] { 0x2A, 0x27, 0xA7, 0xAA })]
    public void Characters_type_their_key_with_shift_when_needed(char character, byte[] expected)
    {
        Assert.Equal(expected, Codes(GuestKey.Of(character)));
    }

    [Fact]
    public void Ctrl_c_holds_ctrl_around_the_c_key()
    {
        Assert.Equal(new byte[] { 0x1D, 0x2E, 0xAE, 0x9D }, Codes(GuestKey.Of('c', control: true)));
        Assert.Equal(new byte[] { 0x1D, 0x2E, 0xAE, 0x9D }, Codes(GuestKey.Of('C', control: true)));
    }

    [Fact]
    public void Alt_is_held_in_three_steps_forty_milliseconds_apart()
    {
        IReadOnlyList<ScancodeStep> steps = GuestKeyboard.Translate(GuestKey.Of('f', alt: true));

        Assert.Equal(3, steps.Count);
        Assert.Equal(new byte[] { 0x38 }, steps[0].Scancodes);
        Assert.Equal(new byte[] { 0x21, 0xA1 }, steps[1].Scancodes);
        Assert.Equal(new byte[] { 0xB8 }, steps[2].Scancodes);
        Assert.Equal(TimeSpan.FromMilliseconds(40), steps[0].PauseAfter);
        Assert.Equal(TimeSpan.FromMilliseconds(40), steps[1].PauseAfter);
        Assert.Equal(TimeSpan.Zero, steps[2].PauseAfter);
    }

    [Theory]
    [InlineData(GuestKeyCode.Enter, new byte[] { 0x1C, 0x9C })]
    [InlineData(GuestKeyCode.Backspace, new byte[] { 0x0E, 0x8E })]
    [InlineData(GuestKeyCode.Tab, new byte[] { 0x0F, 0x8F })]
    [InlineData(GuestKeyCode.Escape, new byte[] { 0x01, 0x81 })]
    [InlineData(GuestKeyCode.Up, new byte[] { 0xE0, 0x48, 0xE0, 0xC8 })]
    [InlineData(GuestKeyCode.Down, new byte[] { 0xE0, 0x50, 0xE0, 0xD0 })]
    [InlineData(GuestKeyCode.Left, new byte[] { 0xE0, 0x4B, 0xE0, 0xCB })]
    [InlineData(GuestKeyCode.Right, new byte[] { 0xE0, 0x4D, 0xE0, 0xCD })]
    [InlineData(GuestKeyCode.Home, new byte[] { 0xE0, 0x47, 0xE0, 0xC7 })]
    [InlineData(GuestKeyCode.End, new byte[] { 0xE0, 0x4F, 0xE0, 0xCF })]
    [InlineData(GuestKeyCode.PageUp, new byte[] { 0xE0, 0x49, 0xE0, 0xC9 })]
    [InlineData(GuestKeyCode.PageDown, new byte[] { 0xE0, 0x51, 0xE0, 0xD1 })]
    [InlineData(GuestKeyCode.Insert, new byte[] { 0xE0, 0x52, 0xE0, 0xD2 })]
    [InlineData(GuestKeyCode.Delete, new byte[] { 0xE0, 0x53, 0xE0, 0xD3 })]
    [InlineData(GuestKeyCode.F1, new byte[] { 0x3B, 0xBB })]
    [InlineData(GuestKeyCode.F10, new byte[] { 0x44, 0xC4 })]
    [InlineData(GuestKeyCode.F11, new byte[] { 0x57, 0xD7 })]
    [InlineData(GuestKeyCode.F12, new byte[] { 0x58, 0xD8 })]
    public void Special_keys_have_their_scancodes(GuestKeyCode code, byte[] expected)
    {
        Assert.Equal(expected, Codes(new GuestKey(code)));
    }

    [Fact]
    public void Shift_tab_holds_shift_and_unknown_characters_type_nothing()
    {
        Assert.Equal(new byte[] { 0x2A, 0x0F, 0x8F, 0xAA }, Codes(new GuestKey(GuestKeyCode.Tab, Shift: true)));
        Assert.Empty(GuestKeyboard.Translate(GuestKey.Of('é')));
        Assert.False(GuestKeyboard.HasKey('é'));
        Assert.True(GuestKeyboard.HasKey('~'));
        Assert.Throws<ArgumentOutOfRangeException>(() => GuestKey.Function(13));
    }

    [Fact]
    public void The_escape_chord_turns_a_letter_into_alt_and_a_digit_into_a_function_key()
    {
        var chord = new EscapeChord();

        Assert.Empty(chord.Feed(new GuestKey(GuestKeyCode.Escape), 0));
        Assert.True(chord.IsPending);
        Assert.Equal([GuestKey.Of('x', alt: true)], chord.Feed(GuestKey.Of('X'), 100));
        Assert.False(chord.IsPending);
        foreach ((char digit, int function) in new[] { ('1', 1), ('9', 9), ('0', 10), ('-', 11), ('=', 12) })
        {
            chord.Feed(new GuestKey(GuestKeyCode.Escape), 0);
            Assert.Equal([GuestKey.Function(function)], chord.Feed(GuestKey.Of(digit), 10));
        }
    }

    [Fact]
    public void The_escape_chord_lets_escape_through_twice_by_timeout_or_before_another_key()
    {
        var chord = new EscapeChord();
        var escape = new GuestKey(GuestKeyCode.Escape);

        chord.Feed(escape, 0);
        Assert.Equal([escape], chord.Feed(escape, 50));

        chord.Feed(escape, 1000);
        Assert.Empty(chord.Tick(1000 + 799));
        Assert.Equal([escape], chord.Tick(1000 + 800));
        Assert.Empty(chord.Tick(5000));

        chord.Feed(escape, 0);
        Assert.Equal([escape, new GuestKey(GuestKeyCode.Enter)], chord.Feed(new GuestKey(GuestKeyCode.Enter), 1));
        chord.Feed(escape, 0);
        Assert.Equal([escape, GuestKey.Of('c', control: true)], chord.Feed(GuestKey.Of('c', control: true), 1));
        chord.Feed(escape, 0);
        Assert.Equal([escape, GuestKey.Of('.')], chord.Feed(GuestKey.Of('.'), 1));
    }

    [Fact]
    public void Keys_without_a_pending_escape_and_alt_keys_pass_through()
    {
        var chord = new EscapeChord();

        Assert.Equal([GuestKey.Of('a')], chord.Feed(GuestKey.Of('a'), 0));
        Assert.Equal([GuestKey.Of('f', alt: true)], chord.Feed(GuestKey.Of('f', alt: true), 0));
        Assert.Equal([new GuestKey(GuestKeyCode.Escape, Alt: true)], chord.Feed(new GuestKey(GuestKeyCode.Escape, Alt: true), 0));
        Assert.False(chord.IsPending);
    }

    [Fact]
    public void Only_the_rows_whose_text_or_colours_changed_are_compared_as_changed()
    {
        EngineTextScreen first = Screen(["hello", "world", "     "], [0x07, 0x07, 0x07], new EngineTextCursor(0, 0, true));

        Assert.Equal([0, 1, 2], TextScreenDiff.ChangedRows(null, first));
        Assert.Empty(TextScreenDiff.ChangedRows(first, first with { }));
        Assert.Equal([1], TextScreenDiff.ChangedRows(first, Screen(["hello", "w0rld", "     "], [0x07, 0x07, 0x07], first.Cursor)));
        Assert.Equal([2], TextScreenDiff.ChangedRows(first, Screen(["hello", "world", "     "], [0x07, 0x07, 0x1E], first.Cursor)));
        Assert.Equal([0, 1, 2, 3], TextScreenDiff.ChangedRows(first, Screen(["a", "b", "c", "d"], [7, 7, 7, 7], first.Cursor)));
        Assert.False(TextScreenDiff.CursorChanged(first, first with { }));
        Assert.True(TextScreenDiff.CursorChanged(first, first with { Cursor = new EngineTextCursor(0, 1, true) }));
        Assert.True(TextScreenDiff.CursorChanged(first, first with { Cursor = new EngineTextCursor(0, 0, false) }));
        Assert.True(TextScreenDiff.CursorChanged(null, first));
        Assert.Empty(TextScreenDiff.ChangedRows(first with { Attributes = null }, first with { Attributes = null }));
    }

    [Fact]
    public void A_screen_gives_the_default_attribute_outside_its_cells()
    {
        EngineTextScreen screen = Screen(["ab"], [0x1E], null);

        Assert.Equal(0x1E, screen.AttributeAt(0, 1));
        Assert.Equal(EngineTextScreen.DefaultAttribute, screen.AttributeAt(0, 5));
        Assert.Equal(EngineTextScreen.DefaultAttribute, screen.AttributeAt(-1, 0));
        Assert.Equal(EngineTextScreen.DefaultAttribute, (screen with { Attributes = null }).AttributeAt(0, 0));
        Assert.Equal("ab", screen.Text);
    }

    /// <summary>A screen of 5 columns, one attribute per row.</summary>
    internal static EngineTextScreen Screen(string[] lines, byte[] rowAttributes, EngineTextCursor? cursor)
    {
        int columns = Math.Max(5, lines.Max(static line => line.Length));
        byte[] attributes = new byte[columns * lines.Length];
        for (int row = 0; row < lines.Length; row++)
        {
            attributes.AsSpan(row * columns, columns).Fill(rowAttributes[row]);
        }

        return new EngineTextScreen(columns, lines.Length, [.. lines.Select(line => line.PadRight(columns))], attributes, cursor);
    }
}
