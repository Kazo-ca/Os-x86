namespace OsX86.Engine;

/// <summary>The two kinds of console ports an engine exposes.</summary>
public enum ConsoleKind
{
    /// <summary>A 16550 UART: <c>ttyS0</c> under Linux, the boot console.</summary>
    Serial = 0,

    /// <summary>A virtio-console port: <c>hvc0</c> to <c>hvc3</c> under Linux (the guest agent and the ptys of the plan, 205 A, 213 A).</summary>
    Virtio = 1,
}

/// <summary>Names one console port of an engine.</summary>
/// <param name="Kind">Serial or virtio.</param>
/// <param name="Index">The port number: 0 for the serial port, 0 to 3 for virtio.</param>
public readonly record struct ConsoleId(ConsoleKind Kind, int Index)
{
    /// <summary>How many virtio-console ports v86 declares.</summary>
    public const int VirtioPortCount = 4;

    /// <summary>The serial port, <c>ttyS0</c>.</summary>
    public static ConsoleId Serial0 { get; } = new(ConsoleKind.Serial, 0);

    /// <summary>The virtio-console port <paramref name="index"/> (0 to 3).</summary>
    public static ConsoleId Virtio(int index) => new(ConsoleKind.Virtio, index);

    /// <summary>The channel name of the worker protocol: <c>serial0</c>, <c>virtio0</c> to <c>virtio3</c>.</summary>
    public string ChannelName => Kind == ConsoleKind.Serial ? $"serial{Index}" : $"virtio{Index}";

    /// <summary>Whether this names a port the engine actually has.</summary>
    public bool IsValid => Kind switch
    {
        ConsoleKind.Serial => Index == 0,
        ConsoleKind.Virtio => Index is >= 0 and < VirtioPortCount,
        _ => false,
    };

    /// <summary>Parses a channel name of the worker protocol.</summary>
    public static bool TryParse(string? channelName, out ConsoleId id)
    {
        id = default;
        if (string.IsNullOrEmpty(channelName))
        {
            return false;
        }

        ConsoleKind kind;
        string digits;
        if (channelName.StartsWith("serial", StringComparison.Ordinal))
        {
            kind = ConsoleKind.Serial;
            digits = channelName["serial".Length..];
        }
        else if (channelName.StartsWith("virtio", StringComparison.Ordinal))
        {
            kind = ConsoleKind.Virtio;
            digits = channelName["virtio".Length..];
        }
        else
        {
            return false;
        }

        if (digits.Length != 1 || !char.IsAsciiDigit(digits[0]))
        {
            return false;
        }

        id = new ConsoleId(kind, digits[0] - '0');
        return id.IsValid;
    }

    /// <inheritdoc />
    public override string ToString() => ChannelName;
}
