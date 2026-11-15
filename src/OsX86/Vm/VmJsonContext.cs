using System.Text.Json;
using System.Text.Json.Serialization;
using OsX86.Interface;
using OsX86.Profiles;

namespace OsX86.Vm;

/// <summary>The serializer context of the profiles and the VM registry (source-generated: the library is trimmed).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(VmProfile))]
[JsonSerializable(typeof(VmRegistryDocument))]
internal sealed partial class VmJsonContext : JsonSerializerContext
{
    /// <summary>The same context with indented output, for files a user reads (<c>.x86profile</c>, the registry).</summary>
    public static VmJsonContext Indented { get; } = new(new JsonSerializerOptions(Default.Options!) { WriteIndented = true });
}

/// <summary>What every versioned document shares (plan §13): a <c>format</c> number read before anything else.</summary>
internal static class VersionedDocument
{
    /// <summary>Reads the <c>format</c> of a JSON document without deserializing it.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.InvalidProfile"/> when the text is not a JSON object with an integer <c>format</c>.</exception>
    public static int PeekFormat(string json, string what)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("format", out JsonElement element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out int format))
            {
                return format;
            }
        }
        catch (JsonException exception)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The {what} is not JSON: {exception.Message}", exception);
        }

        throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The {what} is invalid: 'format' is missing or not an integer.");
    }
}
