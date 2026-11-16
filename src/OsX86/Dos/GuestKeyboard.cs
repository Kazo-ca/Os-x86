namespace OsX86.Dos;

/// <summary>A key of the guest keyboard that is not a character.</summary>
public enum GuestKeyCode
{
    /// <summary>A character key: <see cref="GuestKey.Character"/> says which.</summary>
    Character = 0,

    /// <summary>Enter.</summary>
    Enter,

    /// <summary>Backspace.</summary>
    Backspace,

    /// <summary>Tab (Shift+Tab with <see cref="GuestKey.Shift"/>).</summary>
    Tab,

    /// <summary>Escape.</summary>
    Escape,

    /// <summary>The up arrow.</summary>
    Up,

    /// <summary>The down arrow.</summary>
    Down,

    /// <summary>The left arrow.</summary>
    Left,

    /// <summary>The right arrow.</summary>
    Right,

    /// <summary>Home.</summary>
    Home,

    /// <summary>End.</summary>
    End,

    /// <summary>Page Up.</summary>
    PageUp,

    /// <summary>Page Down.</summary>
    PageDown,

    /// <summary>Insert.</summary>
    Insert,

    /// <summary>Delete.</summary>
    Delete,

    /// <summary>F1; F2 to F12 follow in order.</summary>
    F1,

    /// <summary>F2.</summary>
    F2,

    /// <summary>F3.</summary>
    F3,

    /// <summary>F4.</summary>
    F4,

    /// <summary>F5.</summary>
    F5,

    /// <summary>F6.</summary>
    F6,

    /// <summary>F7.</summary>
    F7,

    /// <summary>F8.</summary>
    F8,

    /// <summary>F9.</summary>
    F9,

    /// <summary>F10.</summary>
    F10,

    /// <summary>F11.</summary>
    F11,

    /// <summary>F12.</summary>
    F12,
}

/// <summary>
/// One key pressed on the guest keyboard, independent of the terminal that typed it (work item 11).
/// </summary>
/// <param name="Code">The key.</param>
/// <param name="Character">For <see cref="GuestKeyCode.Character"/>: the character (printable ASCII; a letter for Ctrl and Alt).</param>
/// <param name="Control">Whether Ctrl is held.</param>
/// <param name="Alt">Whether Alt is held.</param>
/// <param name="Shift">Whether Shift is held (upper-case letters and shifted symbols hold it anyway).</param>
public readonly record struct GuestKey(GuestKeyCode Code, char Character = '\0', bool Control = false, bool Alt = false, bool Shift = false)
{
    /// <summary>A character key.</summary>
    public static GuestKey Of(char character, bool control = false, bool alt = false) => new(GuestKeyCode.Character, character, control, alt);

    /// <summary>A function key, <paramref name="number"/> from 1 to 12.</summary>
    public static GuestKey Function(int number) => number is >= 1 and <= 12
        ? new GuestKey(GuestKeyCode.F1 + (number - 1))
        : throw new ArgumentOutOfRangeException(nameof(number), number, "F1 to F12.");
}

/// <summary>Scancodes sent at once, then a pause before the next ones (a modifier held, E14).</summary>
/// <param name="Scancodes">The scancodes of the step.</param>
/// <param name="PauseAfter">How long to wait before the next step.</param>
public readonly record struct ScancodeStep(IReadOnlyList<byte> Scancodes, TimeSpan PauseAfter);

/// <summary>
/// The PC keyboard of the guest (work item 11; decisions 215 A and 216 A; trial E14): a
/// <see cref="GuestKey"/> turned into PS/2 scancodes of set 1 for a US layout, as v86 takes them.
/// Upper-case letters and shifted symbols hold Shift, Ctrl+key holds Ctrl (Ctrl+C reaches the
/// program, 216 A). Alt is held across three steps with <see cref="AltHold"/> between them: E14
/// measured that EDIT ignores Alt+F sent in one burst and opens its menu when Alt stays down about
/// 40 ms. A character with no key on the US layout gives no scancode.
/// </summary>
public static class GuestKeyboard
{
    /// <summary>How long Alt stays down before and after the key it modifies (E14: about 40 ms).</summary>
    public static readonly TimeSpan AltHold = TimeSpan.FromMilliseconds(40);

    private const byte Extended = 0xE0;
    private const byte Release = 0x80;
    private const byte LeftShift = 0x2A;
    private const byte LeftControl = 0x1D;
    private const byte LeftAlt = 0x38;

    private static readonly Dictionary<char, (byte Code, bool Shift)> Characters = BuildCharacters();

    /// <summary>The scancode steps of a key; empty when the key has no scancode.</summary>
    public static IReadOnlyList<ScancodeStep> Translate(GuestKey key)
    {
        if (!TryMake(key, out byte[] make, out bool shift))
        {
            return [];
        }

        var press = new List<byte>();
        var release = new List<byte>();
        if (key.Control)
        {
            press.Add(LeftControl);
            release.Insert(0, LeftControl | Release);
        }

        if (shift || key.Shift)
        {
            press.Add(LeftShift);
            release.Insert(0, LeftShift | Release);
        }

        byte[] up = BreakOf(make);
        if (!key.Alt)
        {
            return [new ScancodeStep([.. press, .. make, .. up, .. release], TimeSpan.Zero)];
        }

        return
        [
            new ScancodeStep([LeftAlt], AltHold),
            new ScancodeStep([.. press, .. make, .. up, .. release], AltHold),
            new ScancodeStep([LeftAlt | Release], TimeSpan.Zero),
        ];
    }

    /// <summary>Whether a character has a key on the US layout.</summary>
    public static bool HasKey(char character) => Characters.ContainsKey(character);

    private static bool TryMake(GuestKey key, out byte[] make, out bool shift)
    {
        shift = false;
        make = [];
        switch (key.Code)
        {
            case GuestKeyCode.Character:
                char c = key.Control || key.Alt ? char.ToLowerInvariant(key.Character) : key.Character;
                if (!Characters.TryGetValue(c, out (byte Code, bool Shift) found))
                {
                    return false;
                }

                make = [found.Code];
                shift = found.Shift;
                return true;
            case >= GuestKeyCode.F1 and <= GuestKeyCode.F10:
                make = [(byte)(0x3B + (key.Code - GuestKeyCode.F1))];
                return true;
            case GuestKeyCode.F11:
                make = [0x57];
                return true;
            case GuestKeyCode.F12:
                make = [0x58];
                return true;
            default:
                make = key.Code switch
                {
                    GuestKeyCode.Enter => [0x1C],
                    GuestKeyCode.Backspace => [0x0E],
                    GuestKeyCode.Tab => [0x0F],
                    GuestKeyCode.Escape => [0x01],
                    GuestKeyCode.Up => [Extended, 0x48],
                    GuestKeyCode.Down => [Extended, 0x50],
                    GuestKeyCode.Left => [Extended, 0x4B],
                    GuestKeyCode.Right => [Extended, 0x4D],
                    GuestKeyCode.Home => [Extended, 0x47],
                    GuestKeyCode.End => [Extended, 0x4F],
                    GuestKeyCode.PageUp => [Extended, 0x49],
                    GuestKeyCode.PageDown => [Extended, 0x51],
                    GuestKeyCode.Insert => [Extended, 0x52],
                    GuestKeyCode.Delete => [Extended, 0x53],
                    _ => [],
                };
                return make.Length > 0;
        }
    }

    private static byte[] BreakOf(byte[] make) => make.Length == 2 ? [Extended, (byte)(make[1] | Release)] : [(byte)(make[0] | Release)];

    private static Dictionary<char, (byte Code, bool Shift)> BuildCharacters()
    {
        var map = new Dictionary<char, (byte, bool)>();
        const string letters = "qwertyuiop";
        for (int index = 0; index < letters.Length; index++)
        {
            AddLetter(map, letters[index], (byte)(0x10 + index));
        }

        const string middle = "asdfghjkl";
        for (int index = 0; index < middle.Length; index++)
        {
            AddLetter(map, middle[index], (byte)(0x1E + index));
        }

        const string bottom = "zxcvbnm";
        for (int index = 0; index < bottom.Length; index++)
        {
            AddLetter(map, bottom[index], (byte)(0x2C + index));
        }

        const string digits = "1234567890";
        const string shiftedDigits = "!@#$%^&*()";
        for (int index = 0; index < digits.Length; index++)
        {
            map[digits[index]] = ((byte)(0x02 + index), false);
            map[shiftedDigits[index]] = ((byte)(0x02 + index), true);
        }

        foreach ((char plain, char shifted, byte code) in new[]
        {
            ('-', '_', (byte)0x0C), ('=', '+', (byte)0x0D), ('[', '{', (byte)0x1A), (']', '}', (byte)0x1B),
            (';', ':', (byte)0x27), ('\'', '"', (byte)0x28), ('`', '~', (byte)0x29), ('\\', '|', (byte)0x2B),
            (',', '<', (byte)0x33), ('.', '>', (byte)0x34), ('/', '?', (byte)0x35),
        })
        {
            map[plain] = (code, false);
            map[shifted] = (code, true);
        }

        map[' '] = (0x39, false);
        return map;
    }

    private static void AddLetter(Dictionary<char, (byte, bool)> map, char letter, byte code)
    {
        map[letter] = (code, false);
        map[char.ToUpperInvariant(letter)] = (code, true);
    }
}

/// <summary>
/// The Escape chord of decision 215 A, for a terminal without Alt nor function keys: Escape then a
/// letter is Alt+letter, Escape then 1 to 9 and 0 is F1 to F10, Escape then <c>-</c> and <c>=</c> are
/// F11 and F12 (the two keys after 0 on the same row). Escape twice sends one Escape; Escape then any
/// other key sends Escape and that key; an Escape alone is sent after <see cref="Timeout"/>.
/// Keys that already carry Alt (Kazo's Terminal decodes the Meta prefix) pass through untouched.
/// </summary>
public sealed class EscapeChord
{
    /// <summary>How long an Escape waits for the second key of a chord before it is sent alone.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(800);

    private long? _pendingSince;

    /// <summary>Whether an Escape is waiting for the second key.</summary>
    public bool IsPending => _pendingSince is not null;

    /// <summary>The keys to send for a key typed at <paramref name="timestamp"/> (any monotonic clock, in milliseconds).</summary>
    public IReadOnlyList<GuestKey> Feed(GuestKey key, long timestamp)
    {
        bool plainEscape = key.Code == GuestKeyCode.Escape && !key.Alt && !key.Control;
        if (_pendingSince is null)
        {
            if (plainEscape)
            {
                _pendingSince = timestamp;
                return [];
            }

            return [key];
        }

        _pendingSince = null;
        if (plainEscape)
        {
            return [key];
        }

        if (key is { Code: GuestKeyCode.Character, Control: false, Alt: false })
        {
            char c = key.Character;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                return [GuestKey.Of(char.ToLowerInvariant(c), alt: true)];
            }

            int? function = c switch
            {
                >= '1' and <= '9' => c - '0',
                '0' => 10,
                '-' => 11,
                '=' => 12,
                _ => null,
            };
            if (function is { } number)
            {
                return [GuestKey.Function(number)];
            }
        }

        return [new GuestKey(GuestKeyCode.Escape), key];
    }

    /// <summary>The Escape to send alone when it waited <see cref="Timeout"/> at <paramref name="timestamp"/>; empty otherwise.</summary>
    public IReadOnlyList<GuestKey> Tick(long timestamp)
    {
        if (_pendingSince is { } since && timestamp - since >= (long)Timeout.TotalMilliseconds)
        {
            _pendingSince = null;
            return [new GuestKey(GuestKeyCode.Escape)];
        }

        return [];
    }
}
