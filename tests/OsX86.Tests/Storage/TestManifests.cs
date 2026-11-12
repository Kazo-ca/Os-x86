using System.Text.Json;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary>Builds manifest texts for the storage tests: a valid one by default, any field overridable.</summary>
internal static class TestManifests
{
    public const long Size = 100 * 1024 * 1024 + 12345;

    public const int ChunkSize = ImageManifest.DefaultChunkSize;

    /// <summary>A fake SHA-256 for chunk <paramref name="index"/>: 64 lowercase hex digits, distinct per index.</summary>
    public static string FakeHash(int index) => index.ToString("x").PadLeft(64, 'a');

    /// <summary>A valid manifest object (as a mutable dictionary so a test can break one field).</summary>
    public static Dictionary<string, object?> ValidObject(long size = Size, int chunkSize = ChunkSize)
    {
        int chunks = (int)((size + chunkSize - 1) / chunkSize);
        return new Dictionary<string, object?>
        {
            ["format"] = 1,
            ["image"] = "alpine-test",
            ["version"] = "1.0.0",
            ["engine"] = new Dictionary<string, object?> { ["v86"] = "0.5.473" },
            ["size"] = size,
            ["chunkSize"] = chunkSize,
            ["chunks"] = Enumerable.Range(0, chunks).Select(i => new Dictionary<string, object?> { ["sha256"] = FakeHash(i) }).ToList(),
            ["files"] = new Dictionary<string, object?>
            {
                ["kernel"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = Math.Min(4096, size) },
                ["initramfs"] = new Dictionary<string, object?> { ["offset"] = Math.Min(4096, size), ["size"] = Math.Max(0, size - 4096) },
            },
        };
    }

    public static string Json(Dictionary<string, object?> value) => JsonSerializer.Serialize(value);

    /// <summary>A valid manifest text.</summary>
    public static string Valid(long size = Size, int chunkSize = ChunkSize) => Json(ValidObject(size, chunkSize));

    /// <summary>A valid manifest text with one top-level field replaced (or removed when <paramref name="value"/> is <see cref="Removed"/>).</summary>
    public static string With(string field, object? value)
    {
        Dictionary<string, object?> manifest = ValidObject();
        if (ReferenceEquals(value, Removed))
        {
            manifest.Remove(field);
        }
        else
        {
            manifest[field] = value;
        }

        return Json(manifest);
    }

    /// <summary>Marker for <see cref="With"/>: remove the field.</summary>
    public static readonly object Removed = new();
}
