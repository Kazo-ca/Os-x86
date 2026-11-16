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

    /// <summary>
    /// The Wine image (work item 9: linux-base with WineHQ 11.0 i386, Mono, Gecko and the template
    /// prefix), the VM of Windows console programs (work item 10, 194 A). Its default prefix is
    /// <c>default</c> (193 A); <c>x86 run</c> makes it from this profile when the user has none.
    /// </summary>
    public static VmProfile Wine { get; } = new()
    {
        Name = "wine",
        Kind = VmKind.WindowsPrograms,
        Image = "wine",
        ImageVersion = "0.1.0",
        MemoryMiB = 512,
        SerialConsole = true,
        Prefix = "default",
    };

    /// <summary>
    /// The FreeDOS image (work item 4, decisions 111 and 214 A): the VM of DOS programs (work item 11).
    /// No agent and no serial console (212 A: COM1 only carries the start and end of a program run);
    /// the program comes on its own disk and the screen is read from the VGA. <c>x86 run</c> makes it
    /// from this profile when the user has no stopped DOS VM.
    /// </summary>
    public static VmProfile Dos { get; } = new()
    {
        Name = "dos",
        Kind = VmKind.Dos,
        Image = "freedos",
        ImageVersion = "0.2.0",
        MemoryMiB = 64,
        SerialConsole = false,
        Prefix = null,
    };

    /// <summary>Every built-in profile, in display order.</summary>
    public static IReadOnlyList<VmProfile> All { get; } = [AlpineTest, LinuxBase, Wine, Dos];

    /// <summary>Finds a built-in profile by name (ordinal, case-insensitive).</summary>
    public static VmProfile? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
}
