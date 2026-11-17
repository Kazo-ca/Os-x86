using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OsX86.Network;

/// <summary>An IPv4 address, kept as its 32 bits in network order (the first octet in the high byte).</summary>
/// <param name="Value">The address as a number: <c>10.86.0.1</c> is <c>0x0A560001</c>.</param>
public readonly record struct Ipv4Address(uint Value) : IComparable<Ipv4Address>
{
    /// <summary>Makes the address of four octets.</summary>
    public static Ipv4Address Of(byte a, byte b, byte c, byte d) => new(((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d);

    /// <summary>Reads four octets in network order.</summary>
    public static Ipv4Address Read(ReadOnlySpan<byte> bytes) => new(((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3]);

    /// <summary>Writes the four octets in network order.</summary>
    public void Write(Span<byte> destination)
    {
        destination[0] = (byte)(Value >> 24);
        destination[1] = (byte)(Value >> 16);
        destination[2] = (byte)(Value >> 8);
        destination[3] = (byte)Value;
    }

    /// <summary>Reads a dotted address (<c>203.0.113.9</c>): four decimal octets, nothing else.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out Ipv4Address address)
    {
        address = default;
        if (string.IsNullOrEmpty(text) || text.Length > 15)
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        uint value = 0;
        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0') || !part.All(char.IsAsciiDigit)
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet))
            {
                return false;
            }

            value = (value << 8) | octet;
        }

        address = new Ipv4Address(value);
        return true;
    }

    /// <summary>Whether the address is in the network <paramref name="network"/>/<paramref name="prefixLength"/>.</summary>
    public bool IsIn(Ipv4Address network, int prefixLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, 32);
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return (Value & mask) == (network.Value & mask);
    }

    /// <summary>The octet <paramref name="index"/> (0 is the first).</summary>
    public byte Octet(int index) => (byte)(Value >> (24 - (8 * index)));

    /// <inheritdoc />
    public int CompareTo(Ipv4Address other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Octet(0)}.{Octet(1)}.{Octet(2)}.{Octet(3)}");
}

/// <summary>An Ethernet address (48 bits).</summary>
/// <param name="Value">The six octets in the low 48 bits, the first octet highest.</param>
public readonly record struct MacAddress(ulong Value)
{
    /// <summary>The broadcast address <c>ff:ff:ff:ff:ff:ff</c>.</summary>
    public static MacAddress Broadcast { get; } = new(0xFFFF_FFFF_FFFFUL);

    /// <summary>Reads six octets.</summary>
    public static MacAddress Read(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (int index = 0; index < 6; index++)
        {
            value = (value << 8) | bytes[index];
        }

        return new MacAddress(value);
    }

    /// <summary>Writes the six octets.</summary>
    public void Write(Span<byte> destination)
    {
        for (int index = 0; index < 6; index++)
        {
            destination[index] = (byte)(Value >> (40 - (8 * index)));
        }
    }

    /// <summary>Whether the group bit is set (broadcast and multicast frames).</summary>
    public bool IsGroup => ((Value >> 40) & 1) == 1;

    /// <inheritdoc />
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[6];
        Write(bytes);
        return string.Join(':', bytes.ToArray().Select(static octet => octet.ToString("x2", CultureInfo.InvariantCulture)));
    }
}
