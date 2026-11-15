using System.Text.Json;
using System.Text.Json.Serialization;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Profiles;

/// <summary>
/// A VM profile (decision 183 A): the recipe of a VM, not its content (121). A JSON document with
/// the extension <c>.x86profile</c>, versioned (plan §13), that says what the VM runs and with what
/// machine; its <c>programs</c> section is opaque here, reserved to BoxedWine. Format 1:
/// <code>
/// {"format": 1, "name": "alpine-test", "kind": "linux", "image": "alpine-test", "imageVersion": "0.1.0",
///  "memoryMiB": 128, "serialConsole": true, "prefix": "test", "programs": null}
/// </code>
/// Unknown properties are ignored; an unknown <c>format</c> is refused and nothing is written (169).
/// </summary>
public sealed record VmProfile
{
    /// <summary>The only format this version reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The extension of a profile file.</summary>
    public const string FileExtension = ".x86profile";

    /// <summary>The largest profile text accepted, in bytes (the programs section included).</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The longest name of a profile or of a VM.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The format number.</summary>
    public int Format { get; init; } = CurrentFormat;

    /// <summary>The name of the profile, as shown and as given to <c>--profile</c>.</summary>
    public required string Name { get; init; }

    /// <summary>What the VM runs.</summary>
    [JsonConverter(typeof(VmKindJsonConverter))]
    public required VmKind Kind { get; init; }

    /// <summary>The image the VM boots (an identifier of the store, decision 187).</summary>
    public required string Image { get; init; }

    /// <summary>The version of that image.</summary>
    public required string ImageVersion { get; init; }

    /// <summary>The guest memory in MiB; counted against the budget of 165 A.</summary>
    public int MemoryMiB { get; init; } = 128;

    /// <summary>
    /// Whether the image offers a serial console: the only console of an image without agent
    /// (decision 212 A), never offered when the profile does not declare it.
    /// </summary>
    public bool SerialConsole { get; init; }

    /// <summary>
    /// The prefix a console opens in when the caller names none (decision 200 B), or
    /// <see langword="null"/> when the image has no default guest user: the caller must name one.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>The programs section, kept as is for BoxedWine (183 A); never read here.</summary>
    public JsonElement? Programs { get; init; }

    /// <summary>Whether <paramref name="name"/> is a name a profile or a VM may have: 1 to 64 characters, no control character, no leading or trailing blank.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Trim().Length != name.Length)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks the profile; throws <see cref="X86Exception"/> (<see cref="X86ErrorCodes.InvalidProfile"/>) naming the first problem.</summary>
    public void Validate()
    {
        if (Format != CurrentFormat)
        {
            throw new X86Exception(X86ErrorCodes.UnsupportedFormat, $"The profile has format {Format}; this version of Os-x86 reads format {CurrentFormat} only. It is refused and nothing was written.");
        }

        if (!IsValidName(Name))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, "The profile name is missing, blank, longer than 64 characters or has control characters.");
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The kind '{Kind}' is not one of linux, dos, windows-programs.");
        }

        if (!Storage.StorageLayout.IsValidImageId(Image))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The image '{Image}' is not a valid image identifier (lowercase letters, digits, dashes).");
        }

        if (!Storage.StorageLayout.IsValidVersion(ImageVersion))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The image version '{ImageVersion}' is not a valid version.");
        }

        if (MemoryMiB is < Engine.EngineStartOptions.MinimumMemoryMiB or > Engine.EngineStartOptions.MaximumMemoryMiB)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The memory must be between {Engine.EngineStartOptions.MinimumMemoryMiB} and {Engine.EngineStartOptions.MaximumMemoryMiB} MiB, not {MemoryMiB}.");
        }

        if (Prefix is not null && !Agent.LaunchId.IsValidPrefix(Prefix))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The prefix '{Prefix}' is not valid (lowercase letters, digits and dashes, 1 to 16 characters).");
        }
    }

    /// <summary>The JSON text of the profile, indented, as written in a <c>.x86profile</c> file.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, VmJsonContext.Indented.VmProfile);

    /// <summary>Reads and validates a profile text.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.InvalidProfile"/> or <see cref="X86ErrorCodes.UnsupportedFormat"/>.</exception>
    public static VmProfile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The profile is more than the {MaxBytes} bytes accepted.");
        }

        int format = VersionedDocument.PeekFormat(json, "profile");
        if (format != CurrentFormat)
        {
            throw new X86Exception(X86ErrorCodes.UnsupportedFormat, $"The profile has format {format}; this version of Os-x86 reads format {CurrentFormat} only. It is refused and nothing was written.");
        }

        VmProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(json, VmJsonContext.Default.VmProfile);
        }
        catch (JsonException exception)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The profile is invalid: {exception.Message}", exception);
        }

        if (profile is null)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, "The profile is empty.");
        }

        profile.Validate();
        return profile;
    }
}

/// <summary>Writes <see cref="VmKind"/> as <c>linux</c>, <c>dos</c> or <c>windows-programs</c>.</summary>
public sealed class VmKindJsonConverter : JsonConverter<VmKind>
{
    /// <summary>The JSON name of a kind.</summary>
    public static string ToName(VmKind kind) => kind switch
    {
        VmKind.Linux => "linux",
        VmKind.Dos => "dos",
        VmKind.WindowsPrograms => "windows-programs",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Parses a JSON name or a command-line word (<c>linux</c>, <c>dos</c>, <c>windows-programs</c>, <c>windows</c>).</summary>
    public static bool TryParse(string? name, out VmKind kind)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "linux":
                kind = VmKind.Linux;
                return true;
            case "dos":
                kind = VmKind.Dos;
                return true;
            case "windows-programs":
            case "windows":
                kind = VmKind.WindowsPrograms;
                return true;
            default:
                kind = VmKind.Linux;
                return false;
        }
    }

    /// <inheritdoc />
    public override VmKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? name = reader.GetString();
        return TryParse(name, out VmKind kind) ? kind : throw new JsonException($"'{name}' is not a VM kind (linux, dos, windows-programs).");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VmKind value, JsonSerializerOptions options) => writer.WriteStringValue(ToName(value));
}
