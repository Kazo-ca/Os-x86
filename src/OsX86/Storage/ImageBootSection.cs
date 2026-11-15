using System.Collections.ObjectModel;
using System.Text.Json;

namespace OsX86.Storage;

/// <summary>
/// The <c>boot</c> section of an image manifest (the recipes of work item 4 write it), what the
/// engine follows to start the image (work item 6a, <c>ImageBoot.CreateStartOptions</c>):
/// <code>
/// "boot": {"kind": "linux", "cmdline": "root=/dev/sda ro console=ttyS0 ...", "disks": {"hda": "rootfs"},
///          "readyMarker": "OSX86-READY", "prompt": "osx86# ", "memoryMiB": 256, "console": "serial0"}
/// </code>
/// Unknown keys (<c>agent</c>, <c>display</c>, ...) are ignored, as everywhere in the manifest.
/// </summary>
/// <param name="Kind"><c>linux</c> (kernel and initramfs) or <c>dos</c> (the BIOS boots <c>hda</c>); another kind is kept and refused by the engine.</param>
/// <param name="KernelCommandLine">The kernel command line (Linux).</param>
/// <param name="Disks">The disks of the guest: disk name (<c>hda</c>) to logical file of the manifest (<c>rootfs</c>).</param>
/// <param name="ReadyMarker">What the guest prints once it is up (<c>OSX86-READY</c>).</param>
/// <param name="Prompt">The prompt of the serial shell, when there is one.</param>
/// <param name="MemoryMiB">The memory the image asks for.</param>
/// <param name="Console">Where the boot messages go (<c>serial0</c>, or the VGA screen when absent).</param>
public sealed record ImageBootSection(string Kind, string? KernelCommandLine, IReadOnlyDictionary<string, string> Disks, string? ReadyMarker, string? Prompt, int? MemoryMiB, string? Console)
{
    /// <summary>The largest memory a manifest may ask for (the engine bounds it further).</summary>
    public const int MaxMemoryMiB = 65536;

    /// <summary>Reads the section; null when the manifest has none.</summary>
    internal static ImageBootSection? Read(JsonElement root, IReadOnlyDictionary<string, ImageFileEntry> files)
    {
        if (!root.TryGetProperty("boot", out JsonElement boot))
        {
            return null;
        }

        if (boot.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("'boot' must be an object");
        }

        string kind = ReadString(boot, "kind") ?? throw Invalid("'boot.kind' is missing");
        if (!StorageLayout.IsValidFileName(kind))
        {
            throw Invalid("'boot.kind' must be a lowercase name");
        }

        return new ImageBootSection(kind, ReadString(boot, "cmdline"), ReadDisks(boot, files), ReadString(boot, "readyMarker"), ReadString(boot, "prompt"), ReadMemory(boot), ReadString(boot, "console"));
    }

    private static StorageException Invalid(string what) => new(StorageErrorCodes.ManifestInvalid, $"The manifest is invalid: {what}.");

    private static string? ReadString(JsonElement boot, string name)
    {
        if (!boot.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(element.GetString()) || element.GetString()!.Contains('\n', StringComparison.Ordinal))
        {
            throw Invalid($"'boot.{name}' must be a non-empty string on one line");
        }

        return element.GetString();
    }

    private static int? ReadMemory(JsonElement boot)
    {
        if (!boot.TryGetProperty("memoryMiB", out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int memory) || memory is < 1 or > MaxMemoryMiB)
        {
            throw Invalid($"'boot.memoryMiB' must be an integer between 1 and {MaxMemoryMiB}");
        }

        return memory;
    }

    private static ReadOnlyDictionary<string, string> ReadDisks(JsonElement boot, IReadOnlyDictionary<string, ImageFileEntry> files)
    {
        var disks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!boot.TryGetProperty("disks", out JsonElement element))
        {
            return new ReadOnlyDictionary<string, string>(disks);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("'boot.disks' must be an object");
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!StorageLayout.IsValidFileName(property.Name))
            {
                throw Invalid($"'boot.disks' has the disk name '{property.Name}'");
            }

            string? file = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (file is null || !files.ContainsKey(file))
            {
                throw Invalid($"'boot.disks.{property.Name}' must name a file of 'files'");
            }

            if (!disks.TryAdd(property.Name, file))
            {
                throw Invalid($"'boot.disks.{property.Name}' appears twice");
            }
        }

        return new ReadOnlyDictionary<string, string>(disks);
    }
}
