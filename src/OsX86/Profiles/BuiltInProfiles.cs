using OsX86.Interface;

namespace OsX86.Profiles;

/// <summary>
/// The profiles Os-x86 ships (decision 183 A: the ready-made models are delivered in the profile
/// format): one for the Alpine test image with the guest agent (work items 2 and 5) and one for
/// <c>linux-base</c> (work item 4). Their image versions are the ones the recipes build.
/// </summary>
public static class BuiltInProfiles
{
    /// <summary>The Alpine test image: kernel and initramfs, agent on <c>hvc0</c>, prefixes <c>test</c> and <c>other</c>, serial console.</summary>
    public static VmProfile AlpineTest { get; } = new()
    {
        Name = "alpine-test",
        Kind = VmKind.Linux,
        Image = "alpine-test",
        ImageVersion = "0.1.0",
        MemoryMiB = 128,
        SerialConsole = true,
        Prefix = "test",
    };

    /// <summary>The Linux base image (Debian trixie i386, agent installed; its prefix users come with work items 9 and 10).</summary>
    public static VmProfile LinuxBase { get; } = new()
    {
        Name = "linux-base",
        Kind = VmKind.Linux,
        Image = "linux-base",
        ImageVersion = "0.1.0",
        MemoryMiB = 256,
        SerialConsole = true,
        Prefix = null,
    };

    /// <summary>Every built-in profile, in display order.</summary>
    public static IReadOnlyList<VmProfile> All { get; } = [AlpineTest, LinuxBase];

    /// <summary>Finds a built-in profile by name (ordinal, case-insensitive).</summary>
    public static VmProfile? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
}
