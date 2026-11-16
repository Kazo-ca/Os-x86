using OsX86.Interface;
using OsX86.Profiles;

namespace OsX86.Vm;

/// <summary>
/// The VMs of every user of the machine (plan §7: <c>Vm/</c>): the registry, one
/// <see cref="VmInstance"/> per VM that ran, the memory budget shared by all (165 A). Every
/// operation names the user and reaches that user's VMs only (190 A); the budget counts the VMs of
/// every user but names only the caller's. One manager per host (the Feature keeps it; the
/// command of the skeleton shares it through <c>X86Runtime</c>).
/// </summary>
public sealed partial class VmManager : IAsyncDisposable
{
    // Keyed by owner and VM: a GUID copied into another user's registry never reaches this user's instance (190 A).
    private readonly Dictionary<(Guid User, Guid Vm), VmInstance> _instances = [];
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private bool _disposed;

    /// <summary>Held while a start checks the budget and moves to Starting (165 A).</summary>
    internal SemaphoreSlim BudgetGate { get; } = new(1, 1);

    /// <summary>Creates the manager.</summary>
    /// <param name="store">Where the registries are kept.</param>
    /// <param name="engines">Creates the engine of each run.</param>
    /// <param name="images">Finds the boot files of the images.</param>
    /// <param name="host">What the browser says of its memory (165 A).</param>
    /// <param name="options">Delays and limits.</param>
    /// <param name="time">The clock, replaced by the tests.</param>
    /// <param name="network">
    /// The private networks of the users (work item 12); <see langword="null"/>: the VMs get no
    /// network card. The manager disposes it with itself.
    /// </param>
    public VmManager(IVmRegistryStore store, IEngineFactory engines, IBootImageSource images, IHostMemory host, VmManagerOptions? options = null, TimeProvider? time = null, Network.NetworkHub? network = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(host);
        Options = options ?? new VmManagerOptions();
        Options.Validate();
        _time = time ?? TimeProvider.System;
        Registry = new VmRegistry(store, _time);
        Engines = engines;
        Images = images;
        Budget = new MemoryBudget(host, Options.MemoryLimitMiB);
        Network = network;
    }

    /// <summary>The private networks of the users, or <see langword="null"/> when the VMs have no network.</summary>
    public Network.NetworkHub? Network { get; }

    /// <summary>Raised when any VM changes state; the service of a user filters its own.</summary>
    public event EventHandler<VmInstanceEventArgs<VmStateChangedEventArgs>>? InstanceStateChanged;

    /// <summary>Raised when the watchdog of a VM gives up on its agent.</summary>
    public event EventHandler<VmInstanceEventArgs<VmAgentLostEventArgs>>? AgentLost;

    /// <summary>Raised while a VM that starts waits for its image to download (work item 6b); never when the image is already there.</summary>
    public event EventHandler<VmInstanceEventArgs<Storage.DownloadProgress>>? ImageProgress;

    /// <summary>Raised for an error of a background task of a VM (a stop after the grace that failed).</summary>
    public event EventHandler<VmInstanceEventArgs<Exception>>? BackgroundError;

    /// <summary>The registry.</summary>
    public VmRegistry Registry { get; }

    /// <summary>The engine factory.</summary>
    public IEngineFactory Engines { get; }

    /// <summary>The image source.</summary>
    public IBootImageSource Images { get; }

    /// <summary>The memory budget.</summary>
    public MemoryBudget Budget { get; }

    /// <summary>The options.</summary>
    public VmManagerOptions Options { get; }

    /// <summary>The user's VMs with their state.</summary>
    public async ValueTask<IReadOnlyList<VmSummary>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<VmRecord> records = await Registry.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return [.. records.Select(record => record.ToSummary(StateOf(userId, record.Id)))];
    }

    /// <summary>The state of a VM of a user: stopped when it never ran.</summary>
    public VmState StateOf(Guid userId, Guid vmId)
    {
        lock (_lock)
        {
            return _instances.TryGetValue((userId, vmId), out VmInstance? instance) ? instance.State : VmState.Stopped;
        }
    }

    /// <summary>The instances that are starting or running, of every user.</summary>
    public IReadOnlyList<VmInstance> Running()
    {
        lock (_lock)
        {
            return [.. _instances.Values.Where(instance => instance.State is VmState.Starting or VmState.Running)];
        }
    }

    /// <summary>Creates a VM from a profile.</summary>
    public async ValueTask<VmSummary> CreateAsync(Guid userId, string name, VmProfile profile, CancellationToken cancellationToken)
    {
        VmRecord record = await Registry.CreateAsync(userId, name, profile, cancellationToken).ConfigureAwait(false);
        return record.ToSummary(VmState.Stopped);
    }

    /// <summary>The VM of the user named by its GUID or its name.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>.</exception>
    public ValueTask<VmRecord> ResolveAsync(Guid userId, string nameOrId, CancellationToken cancellationToken) =>
        Registry.ResolveAsync(userId, nameOrId, cancellationToken);

    /// <summary>The instance of a VM of the user, created on first use.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>: not the user's VM.</exception>
    public async ValueTask<VmInstance> GetInstanceAsync(Guid userId, Guid vmId, CancellationToken cancellationToken)
    {
        VmRecord record = await Registry.FindAsync(userId, vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No virtual machine with the identifier {vmId}.");
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_instances.TryGetValue((userId, vmId), out VmInstance? instance))
            {
                instance = new VmInstance(userId, record, this, Options, _time);
                instance.StateChanged += (sender, e) => InstanceStateChanged?.Invoke(this, new VmInstanceEventArgs<VmStateChangedEventArgs>((VmInstance)sender!, e));
                instance.AgentLost += (sender, e) => AgentLost?.Invoke(this, new VmInstanceEventArgs<VmAgentLostEventArgs>((VmInstance)sender!, e));
                _instances[(userId, vmId)] = instance;
            }
            else
            {
                instance.Record = record;
            }

            return instance;
        }
    }

    /// <summary>Renames a VM; allowed while it runs.</summary>
    public async ValueTask<VmSummary> RenameAsync(Guid userId, Guid vmId, string name, CancellationToken cancellationToken)
    {
        VmRecord renamed = await Registry.RenameAsync(userId, vmId, name, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_instances.TryGetValue((userId, vmId), out VmInstance? instance))
            {
                instance.Record = renamed;
            }
        }

        return renamed.ToSummary(StateOf(userId, vmId));
    }

    /// <summary>
    /// Removes a VM; a running one is refused unless <paramref name="force"/>, which stops it first.
    /// Its write layers go with it (work item 6b): deleted before the record, so that a failure
    /// leaves the VM listed and the removal can be tried again.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, <see cref="X86ErrorCodes.VmBusy"/>, <see cref="X86ErrorCodes.StorageFailed"/>.</exception>
    public async ValueTask RemoveAsync(Guid userId, Guid vmId, bool force, CancellationToken cancellationToken)
    {
        VmRecord record = await Registry.FindAsync(userId, vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No virtual machine with the identifier {vmId}.");
        VmInstance? instance;
        lock (_lock)
        {
            _instances.TryGetValue((userId, vmId), out instance);
        }

        // Stopped for good under its own lifecycle lock: no application can start it again between
        // the stop and the removal, which would leave an engine the budget no longer sees.
        if (instance is not null)
        {
            await instance.RetireAsync(force).ConfigureAwait(false);
        }

        try
        {
            await Images.DeleteLayersAsync(record.EffectiveLayerSet, cancellationToken).ConfigureAwait(false);
            await Registry.RemoveAsync(userId, vmId, cancellationToken).ConfigureAwait(false);
            if (Network is not null)
            {
                await Network.For(userId).ForgetAsync(vmId, null, cancellationToken).ConfigureAwait(false); // a new VM never inherits its rules
            }
        }
        finally
        {
            lock (_lock)
            {
                _instances.Remove((userId, vmId));
            }
        }
    }

    /// <summary>Starts a VM by hand (72.6).</summary>
    public async ValueTask HoldAsync(Guid userId, Guid vmId, CancellationToken cancellationToken) =>
        await (await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false)).HoldAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Stops a VM (see <see cref="VmInstance.StopAsync"/>).</summary>
    public async ValueTask StopAsync(Guid userId, Guid vmId, bool force, CancellationToken cancellationToken) =>
        await (await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false)).StopAsync(force, cancellationToken).ConfigureAwait(false);

    /// <summary>Opens a console in a VM (see <see cref="VmInstance.OpenConsoleAsync"/>).</summary>
    public async ValueTask<IVmConsole> OpenConsoleAsync(Guid userId, Guid vmId, VmConsoleOptions options, CancellationToken cancellationToken) =>
        await (await GetInstanceAsync(userId, vmId, cancellationToken).ConfigureAwait(false)).OpenConsoleAsync(options, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Refuses the start of <paramref name="starting"/> when the memory of the VMs starting or running
    /// plus its own would exceed the budget (165 A). The refusal names the caller's running VMs and
    /// those compatible with the refused one (same kind, same image); other users' VMs are only counted.
    /// </summary>
    /// <exception cref="VmMemoryBudgetExceededException">The budget would be exceeded.</exception>
    internal void CheckBudget(VmInstance starting)
    {
        VmRecord record = starting.Record;
        List<VmInstance> running;
        lock (_lock)
        {
            // A VM that is stopping still holds its engine: it is counted until it is stopped.
            running = [.. _instances.Values.Where(instance => !ReferenceEquals(instance, starting) && instance.State is VmState.Starting or VmState.Running or VmState.Stopping)];
        }

        int runningMiB = running.Sum(instance => instance.Record.Profile.MemoryMiB);
        if (Budget.Fits(runningMiB, record.Profile.MemoryMiB))
        {
            return;
        }

        Guid user = starting.OwnerId;
        List<VmSummary> own = [.. running.Where(instance => instance.OwnerId == user).Select(instance => instance.Summary)];
        List<VmSummary> compatible = [.. own.Where(vm => vm.Kind == record.Profile.Kind && vm.Image == record.Profile.Image)];
        throw new VmMemoryBudgetExceededException(record.Profile.MemoryMiB, runningMiB, Budget.LimitMiB, own, compatible);
    }

    internal void ReportImageProgress(VmInstance instance, Storage.DownloadProgress progress)
    {
        try
        {
            ImageProgress?.Invoke(this, new VmInstanceEventArgs<Storage.DownloadProgress>(instance, progress));
        }
        catch (Exception exception)
        {
            try
            {
                ReportBackgroundError(instance, exception); // a listener that fails never fails the download
            }
            catch (Exception)
            {
                // Neither does a failing error listener.
            }
        }
    }

    internal void ReportBackgroundError(VmInstance instance, Exception exception) =>
        BackgroundError?.Invoke(this, new VmInstanceEventArgs<Exception>(instance, exception));

    /// <summary>Stops every VM at once and releases the engines.</summary>
    public async ValueTask DisposeAsync()
    {
        List<VmInstance> instances;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            instances = [.. _instances.Values];
        }

        foreach (VmInstance instance in instances)
        {
            try
            {
                await instance.RetireAsync(force: true).ConfigureAwait(false); // and no start after it
            }
            catch (Exception exception)
            {
                ReportBackgroundError(instance, exception);
            }
        }

        if (Network is not null)
        {
            await Network.DisposeAsync().ConfigureAwait(false);
        }
    }

}

/// <summary>An event of one instance, with the instance.</summary>
public sealed class VmInstanceEventArgs<T>(VmInstance instance, T data) : EventArgs
{
    /// <summary>The VM.</summary>
    public VmInstance Instance { get; } = instance;

    /// <summary>The event.</summary>
    public T Data { get; } = data;
}
