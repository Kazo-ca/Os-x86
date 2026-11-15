namespace OsX86.Vm;

/// <summary>What the host says of its memory: <c>navigator.deviceMemory</c> in a browser, or nothing.</summary>
public interface IHostMemory
{
    /// <summary>The memory the browser announces, in bytes, or <see langword="null"/> when it says nothing (Firefox, Safari).</summary>
    long? AnnouncedBytes { get; }
}

/// <summary>A host memory source with a fixed answer: tests, and hosts that cannot ask the browser.</summary>
public sealed class FixedHostMemory(long? announcedBytes) : IHostMemory
{
    /// <summary>A host that announces nothing.</summary>
    public static FixedHostMemory Unknown { get; } = new(null);

    /// <inheritdoc />
    public long? AnnouncedBytes { get; } = announcedBytes;
}

/// <summary>
/// The memory budget of the running VMs (decision 165 A): half of the memory the browser announces
/// by default, or the limit the administrator set (work item 7 writes it; here it is read). A start
/// is refused, without any question, only when the memory of every running VM plus the new one
/// would exceed the budget.
/// </summary>
public sealed class MemoryBudget
{
    /// <summary>What is assumed when the browser announces nothing: the floor of the plan (§17: 8 GB machines) halved, so the budget stays 2 GiB.</summary>
    public const int DefaultAnnouncedMiB = 4096;

    /// <summary>Creates the budget.</summary>
    /// <param name="host">What the browser announces.</param>
    /// <param name="configuredLimitMiB">The administrator's limit, or <see langword="null"/> for the default of 165 A.</param>
    public MemoryBudget(IHostMemory host, int? configuredLimitMiB = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (configuredLimitMiB is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredLimitMiB), configuredLimitMiB, "The limit must be positive.");
        }

        long? announced = host.AnnouncedBytes;
        AnnouncedMiB = announced is > 0 ? (int)Math.Min(int.MaxValue, announced.Value / (1024 * 1024)) : DefaultAnnouncedMiB;
        AnnouncedByHost = announced is > 0;
        ConfiguredLimitMiB = configuredLimitMiB;
    }

    /// <summary>The memory of the machine in MiB, as announced, or <see cref="DefaultAnnouncedMiB"/>.</summary>
    public int AnnouncedMiB { get; }

    /// <summary>Whether <see cref="AnnouncedMiB"/> comes from the browser rather than the default.</summary>
    public bool AnnouncedByHost { get; }

    /// <summary>The administrator's limit, when set.</summary>
    public int? ConfiguredLimitMiB { get; }

    /// <summary>The budget: <see cref="ConfiguredLimitMiB"/>, or half of <see cref="AnnouncedMiB"/>.</summary>
    public int LimitMiB => ConfiguredLimitMiB ?? AnnouncedMiB / 2;

    /// <summary>Whether a VM of <paramref name="requestedMiB"/> fits next to <paramref name="runningMiB"/> of running VMs.</summary>
    public bool Fits(int runningMiB, int requestedMiB) => (long)runningMiB + requestedMiB <= LimitMiB;
}
