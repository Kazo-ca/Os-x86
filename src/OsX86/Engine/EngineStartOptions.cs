namespace OsX86.Engine;

/// <summary>
/// What an engine boots, all of it read from the origin private file system (OPFS, the only
/// storage fast enough for a VM, trial E2): a Linux kernel and its initramfs (<see cref="EngineBootKind.Linux"/>)
/// or a disk the BIOS boots (<see cref="EngineBootKind.Dos"/>), the IDE disks of the guest with the
/// layer that keeps their writes, and the machine around them. <c>ImageBoot.CreateStartOptions</c>
/// builds them from the <c>boot</c> section of an image manifest; the whole-file paths
/// <see cref="KernelPath"/> and <see cref="InitrdPath"/> of work item 2 remain.
/// </summary>
public sealed record EngineStartOptions
{
    /// <summary>The smallest memory v86 boots a Linux kernel with.</summary>
    public const int MinimumMemoryMiB = 16;

    /// <summary>The largest memory the engine accepts (plan: a VM stays well under the browser's budget).</summary>
    public const int MaximumMemoryMiB = 4096;

    /// <summary>The default size of the cache of decompressed frames shared by the disks.</summary>
    public const int DefaultFrameCacheMiB = 32;

    /// <summary>The smallest frame cache.</summary>
    public const int MinimumFrameCacheMiB = 2;

    /// <summary>The largest frame cache.</summary>
    public const int MaximumFrameCacheMiB = 512;

    /// <summary>The default kernel command line: serial console, v86's stable TSC, no mitigations in an emulator.</summary>
    public const string DefaultKernelCommandLine = "console=ttyS0 tsc=reliable mitigations=off";

    /// <summary>What boots.</summary>
    public EngineBootKind Kind { get; init; } = EngineBootKind.Linux;

    /// <summary>OPFS path of a kernel (bzImage) read whole; or give <see cref="Kernel"/>.</summary>
    public string? KernelPath { get; init; }

    /// <summary>OPFS path of an initramfs read whole; or give <see cref="Initrd"/>.</summary>
    public string? InitrdPath { get; init; }

    /// <summary>The kernel as a source (a range of an image payload, compressed or not).</summary>
    public EngineFileSource? Kernel { get; init; }

    /// <summary>The initramfs as a source.</summary>
    public EngineFileSource? Initrd { get; init; }

    /// <summary>The disks of the guest by name (<c>hda</c>, <c>hdb</c>).</summary>
    public IReadOnlyDictionary<string, EngineDiskOptions> Disks { get; init; } = new Dictionary<string, EngineDiskOptions>();

    /// <summary>The kernel command line (Linux only).</summary>
    public string KernelCommandLine { get; init; } = DefaultKernelCommandLine;

    /// <summary>Guest memory in MiB (<see cref="MinimumMemoryMiB"/> to <see cref="MaximumMemoryMiB"/>).</summary>
    public int MemoryMiB { get; init; } = 128;

    /// <summary>VGA memory in MiB (1 to 64).</summary>
    public int VgaMemoryMiB { get; init; } = 2;

    /// <summary>The bound of the cache of decompressed disk frames, in MiB.</summary>
    public int FrameCacheMiB { get; init; } = DefaultFrameCacheMiB;

    /// <summary>Whether the guest gets the virtio-console device (four ports, <c>hvc0</c> to <c>hvc3</c> under Linux).</summary>
    public bool VirtioConsole { get; init; } = true;

    /// <summary>
    /// Whether scancodes may be sent to the guest's PS/2 keyboard (<see cref="ITextModeEngine.SendScancodesAsync"/>):
    /// the DOS way of work item 11 only (215 A); the other guests are reached through their consoles.
    /// </summary>
    public bool Keyboard { get; init; }

    /// <summary>The kernel to boot: <see cref="Kernel"/>, or the whole file of <see cref="KernelPath"/>.</summary>
    public EngineFileSource? EffectiveKernel => Kernel ?? (KernelPath is null ? null : EngineFileSource.WholeFile(KernelPath));

    /// <summary>The initramfs to boot: <see cref="Initrd"/>, or the whole file of <see cref="InitrdPath"/>.</summary>
    public EngineFileSource? EffectiveInitrd => Initrd ?? (InitrdPath is null ? null : EngineFileSource.WholeFile(InitrdPath));

    /// <summary>Checks the options; throws <see cref="ArgumentException"/> naming the first invalid one.</summary>
    public void Validate()
    {
        ValidateBootFiles();
        ValidateDisks();
        if (MemoryMiB is < MinimumMemoryMiB or > MaximumMemoryMiB)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryMiB), MemoryMiB, $"The memory must be between {MinimumMemoryMiB} and {MaximumMemoryMiB} MiB.");
        }

        if (VgaMemoryMiB is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(VgaMemoryMiB), VgaMemoryMiB, "The VGA memory must be between 1 and 64 MiB.");
        }

        if (FrameCacheMiB is < MinimumFrameCacheMiB or > MaximumFrameCacheMiB)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameCacheMiB), FrameCacheMiB, $"The frame cache must be between {MinimumFrameCacheMiB} and {MaximumFrameCacheMiB} MiB.");
        }

        if (string.IsNullOrWhiteSpace(KernelCommandLine) || KernelCommandLine.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("The kernel command line must be a single non-empty line.", nameof(KernelCommandLine));
        }
    }

    private void ValidateBootFiles()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown boot kind.");
        }

        if (KernelPath is not null && Kernel is not null)
        {
            throw new ArgumentException("Give the kernel as a path or as a source, not both.", nameof(Kernel));
        }

        if (InitrdPath is not null && Initrd is not null)
        {
            throw new ArgumentException("Give the initramfs as a path or as a source, not both.", nameof(Initrd));
        }

        if (Kind == EngineBootKind.Linux)
        {
            (EffectiveKernel ?? throw new ArgumentException("A Linux boot needs a kernel.", nameof(Kernel))).Validate(Kernel is null ? nameof(KernelPath) : nameof(Kernel));
            (EffectiveInitrd ?? throw new ArgumentException("A Linux boot needs an initramfs.", nameof(Initrd))).Validate(Initrd is null ? nameof(InitrdPath) : nameof(Initrd));
        }
        else if (EffectiveKernel is not null || EffectiveInitrd is not null)
        {
            throw new ArgumentException("A DOS boot starts from its disk: no kernel nor initramfs.", nameof(Kernel));
        }
    }

    private void ValidateDisks()
    {
        if (Disks is null)
        {
            throw new ArgumentNullException(nameof(Disks));
        }

        foreach ((string name, EngineDiskOptions disk) in Disks)
        {
            if (!EngineDiskOptions.Names.Contains(name))
            {
                throw new ArgumentException($"The engine has no disk '{name}' ({string.Join(", ", EngineDiskOptions.Names)}).", nameof(Disks));
            }

            if (disk?.Source is null || disk.Layer is null)
            {
                throw new ArgumentException($"The disk {name} needs a source and a layer.", nameof(Disks));
            }

            disk.Source.Validate(nameof(Disks));
        }

        if (Kind == EngineBootKind.Dos && !Disks.ContainsKey("hda"))
        {
            throw new ArgumentException("A DOS boot needs the disk hda.", nameof(Disks));
        }
    }
}
