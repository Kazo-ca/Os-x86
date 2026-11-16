using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using OsX86.Agent;

namespace OsX86.Prefixes;

/// <summary>
/// The name of a prefix (decision 200 B: "prefix", Wine's word; "préfixe" in French): lowercase
/// letters, digits and dashes, 1 to 16 characters, unique in its VM. Its guest user is
/// <c>osx86-&lt;name&gt;</c> (decisions 144 and 193 A: one guest user per prefix, chosen by Os-x86).
/// </summary>
[JsonConverter(typeof(PrefixIdJsonConverter))]
public readonly record struct PrefixId
{
    private readonly string? _value;

    /// <summary>Creates the identifier.</summary>
    /// <exception cref="ArgumentException">The name is not valid.</exception>
    public PrefixId(string value)
    {
        if (!LaunchId.IsValidPrefix(value))
        {
            throw new ArgumentException($"'{value}' is not a prefix name (lowercase letters, digits and dashes, 1 to {LaunchId.MaxPrefixLength} characters).", nameof(value));
        }

        _value = value;
    }

    /// <summary>The prefix Os-x86 chooses when the profile names none and the caller gives none (193 A).</summary>
    public static PrefixId Default { get; } = new("default");

    /// <summary>The name.</summary>
    public string Value => _value ?? Default._value!;

    /// <summary>The guest user of the prefix.</summary>
    public string GuestUser => "osx86-" + Value;

    /// <summary>Reads a prefix name.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out PrefixId id)
    {
        if (LaunchId.IsValidPrefix(text))
        {
            id = new PrefixId(text!);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A prefix of a VM, as the registry keeps it (the guest keeps its user and files).</summary>
/// <param name="Id">Its name.</param>
/// <param name="Created">When the user created it, or when Os-x86 made the default prefix on first use.</param>
public sealed record Prefix(PrefixId Id, DateTimeOffset Created);

/// <summary>Writes a <see cref="PrefixId"/> as its name; refuses an invalid one.</summary>
internal sealed class PrefixIdJsonConverter : JsonConverter<PrefixId>
{
    public override PrefixId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PrefixId.TryParse(reader.TokenType == JsonTokenType.String ? reader.GetString() : null, out PrefixId id)
            ? id
            : throw new JsonException("A prefix name is lowercase letters, digits and dashes, 1 to 16 characters.");

    public override void Write(Utf8JsonWriter writer, PrefixId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
