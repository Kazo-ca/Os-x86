namespace OsX86.Engine;

/// <summary>
/// What an engine boots: a kernel and an initramfs read from the origin private file system
/// (OPFS, the only storage fast enough for a VM, trial E2), and the machine around them. Disk
/// images, networking and the display come with the next work items.
/// </summary>
public sealed record EngineStartOptions
{
    /// <summary>The smallest memory v86 boots a Linux kernel with.</summary>
    public const int MinimumMemoryMiB = 16;

    /// <summary>The largest memory the engine accepts (plan: a VM stays well under the browser's budget).</summary>
    public const int MaximumMemoryMiB = 4096;

    /// <summary>The default kernel command line: serial console, v86's stable TSC, no mitigations in an emulator.</summary>
    public const string DefaultKernelCommandLine = "console=ttyS0 tsc=reliable mitigations=off";

    /// <summary>OPFS path of the kernel (bzImage), relative to the origin's root, with <c>/</c> separators.</summary>
    public required string KernelPath { get; init; }

    /// <summary>OPFS path of the initramfs.</summary>
    public required string InitrdPath { get; init; }

    /// <summary>The kernel command line.</summary>
    public string KernelCommandLine { get; init; } = DefaultKernelCommandLine;

    /// <summary>Guest memory in MiB (<see cref="MinimumMemoryMiB"/> to <see cref="MaximumMemoryMiB"/>).</summary>
    public int MemoryMiB { get; init; } = 128;

    /// <summary>VGA memory in MiB (1 to 64).</summary>
    public int VgaMemoryMiB { get; init; } = 2;

    /// <summary>Whether the guest gets the virtio-console device (four ports, <c>hvc0</c> to <c>hvc3</c> under Linux).</summary>
    public bool VirtioConsole { get; init; } = true;

    /// <summary>Checks the options; throws <see cref="ArgumentException"/> naming the first invalid one.</summary>
    public void Validate()
    {
        ValidatePath(KernelPath, nameof(KernelPath));
        ValidatePath(InitrdPath, nameof(InitrdPath));
        if (MemoryMiB is < MinimumMemoryMiB or > MaximumMemoryMiB)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryMiB), MemoryMiB, $"The memory must be between {MinimumMemoryMiB} and {MaximumMemoryMiB} MiB.");
        }

        if (VgaMemoryMiB is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(VgaMemoryMiB), VgaMemoryMiB, "The VGA memory must be between 1 and 64 MiB.");
        }

        if (string.IsNullOrWhiteSpace(KernelCommandLine) || KernelCommandLine.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("The kernel command line must be a single non-empty line.", nameof(KernelCommandLine));
        }
    }

    private static void ValidatePath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An OPFS path is required.", name);
        }

        if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"'{path}' is not a relative OPFS path (segments separated by '/', no '.', '..' or empty segment).", name);
        }
    }
}
