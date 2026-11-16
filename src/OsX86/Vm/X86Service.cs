using OsX86.Interface;
using OsX86.Programs;

namespace OsX86.Vm;

/// <summary>
/// The implementation of <see cref="Ix86"/> that the feature hands to an app (plan §1.1, x86-Lib):
/// the <see cref="VmManager"/> of the machine seen by one user (decision 190 A). Every call reaches
/// that user's VMs only; state changes of other users' VMs are never raised.
/// </summary>
public sealed class X86Service : Ix86
{
    /// <summary>The version of the feature, kept in one place for the command and the contract.</summary>
    public const string FeatureVersion = "0.1.0";

    private readonly Lock _lock = new();
    private EventHandler<VmStateChangedEventArgs>? _stateChanged;

    /// <summary>Creates the service of one user on the machine's manager.</summary>
    /// <param name="manager">The VMs of the machine.</param>
    /// <param name="userId">The stable identifier of the calling user (190 A); never empty.</param>
    public X86Service(VmManager manager, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (userId == Guid.Empty)
        {
            throw new X86Exception(X86ErrorCodes.InvalidUser, "The user identifier is empty.");
        }

        Manager = manager;
        UserId = userId;
    }

    /// <summary>The VMs of the machine.</summary>
    public VmManager Manager { get; }

    /// <summary>The user this service acts for.</summary>
    public Guid UserId { get; }

    /// <inheritdoc />
    public string Version => FeatureVersion;

    /// <inheritdoc />
    public event EventHandler<VmStateChangedEventArgs>? VmStateChanged
    {
        add
        {
            lock (_lock)
            {
                if (_stateChanged is null)
                {
                    Manager.InstanceStateChanged += OnInstanceStateChanged;
                }

                _stateChanged += value;
            }
        }

        remove
        {
            lock (_lock)
            {
                _stateChanged -= value;
                if (_stateChanged is null)
                {
                    Manager.InstanceStateChanged -= OnInstanceStateChanged;
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<VmSummary>> GetVmsAsync(CancellationToken cancellationToken) => Manager.ListAsync(UserId, cancellationToken);

    /// <inheritdoc />
    public ValueTask StartVmAsync(Guid vmId, CancellationToken cancellationToken) => Manager.HoldAsync(UserId, vmId, cancellationToken);

    /// <inheritdoc />
    public ValueTask StopVmAsync(Guid vmId, bool force, CancellationToken cancellationToken) => Manager.StopAsync(UserId, vmId, force, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IVmConsole> OpenConsoleAsync(Guid vmId, VmConsoleOptions options, CancellationToken cancellationToken) =>
        Manager.OpenConsoleAsync(UserId, vmId, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ProgramDetection> DetectProgramAsync(ReadOnlyMemory<byte> header, string? fileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProgramDetector.Detect(header.Span, ProgramDetector.ExtensionOf(fileName) ?? fileName));
    }

    /// <inheritdoc />
    public ValueTask<IVmConsole> RunProgramAsync(ProgramRunOptions options, CancellationToken cancellationToken) =>
        Manager.RunProgramAsync(UserId, options, cancellationToken);

    private void OnInstanceStateChanged(object? sender, VmInstanceEventArgs<VmStateChangedEventArgs> e)
    {
        if (e.Instance.OwnerId != UserId)
        {
            return;
        }

        EventHandler<VmStateChangedEventArgs>? handlers;
        lock (_lock)
        {
            handlers = _stateChanged;
        }

        handlers?.Invoke(this, e.Data);
    }
}
