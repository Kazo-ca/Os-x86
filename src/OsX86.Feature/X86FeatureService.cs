using Kazo.Os.AppSdk.Features;
using OsX86.Interface;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>
/// The <see cref="Ix86"/> Kazo OS hands to apps through <c>KazoFeatures.GetServiceAsync&lt;Ix86&gt;()</c>
/// (ADR 0262, work item 8). Kazo creates one instance per page, shared by every app of every user,
/// so nothing here belongs to a user: each operation reads the app that calls
/// (<see cref="IFeatureServiceContext.Caller"/>), checks its permission (ADR 0246, decision 176 A)
/// and acts on that app's user's VMs only (190 A), through an <see cref="X86Service"/> on the
/// machine's <see cref="VmManager"/>.
/// </summary>
public sealed class X86FeatureService : Ix86
{
    private readonly IFeatureServiceContext _context;
    private readonly Func<VmManager> _manager;
    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>Creates the service; called by Kazo on the first request of the page.</summary>
    /// <param name="context">What Kazo gives a feature's service: the caller of each operation.</param>
    public X86FeatureService(IFeatureServiceContext context)
        : this(context, static () => X86Runtime.Services.Manager)
    {
    }

    /// <summary>Creates the service on a given manager (tests).</summary>
    internal X86FeatureService(IFeatureServiceContext context, Func<VmManager> manager)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);
        _context = context;
        _manager = manager;
    }

    /// <inheritdoc />
    public string Version => X86Service.FeatureVersion;

    /// <inheritdoc />
    /// <remarks>
    /// Each handler hears the state changes of the VMs of the user whose app added it, never another
    /// user's; adding one requires <see cref="X86Permissions.VmUse"/>. The sender is this service.
    /// </remarks>
    public event EventHandler<VmStateChangedEventArgs>? VmStateChanged
    {
        add
        {
            if (value is null)
            {
                return;
            }

            X86Service user = For(X86Permissions.VmUse);
            EventHandler<VmStateChangedEventArgs> relay = (_, e) => value(this, e);
            lock (_lock)
            {
                _subscriptions.Add(new Subscription(value, user, relay));
            }

            user.VmStateChanged += relay;
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            Subscription? subscription;
            lock (_lock)
            {
                // The last one added first, like a multicast delegate removes its last match.
                int index = _subscriptions.FindLastIndex(candidate => candidate.Handler == value);
                if (index < 0)
                {
                    return;
                }

                subscription = _subscriptions[index];
                _subscriptions.RemoveAt(index);
            }

            subscription.User.VmStateChanged -= subscription.Relay;
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<VmSummary>> GetVmsAsync(CancellationToken cancellationToken) =>
        For(X86Permissions.VmUse).GetVmsAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask StartVmAsync(Guid vmId, CancellationToken cancellationToken) =>
        For(X86Permissions.VmUse).StartVmAsync(vmId, cancellationToken);

    /// <inheritdoc />
    public ValueTask StopVmAsync(Guid vmId, bool force, CancellationToken cancellationToken) =>
        For(X86Permissions.VmUse).StopVmAsync(vmId, force, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IVmConsole> OpenConsoleAsync(Guid vmId, VmConsoleOptions options, CancellationToken cancellationToken) =>
        For(X86Permissions.VmUse).OpenConsoleAsync(vmId, options, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Pure, starts nothing: no permission (decision 240 D: the shell asks before choosing an app).</remarks>
    public ValueTask<ProgramDetection> DetectProgramAsync(ReadOnlyMemory<byte> header, string? fileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Programs.ProgramDetector.Detect(header.Span, Programs.ProgramDetector.ExtensionOf(fileName) ?? fileName));
    }

    /// <inheritdoc />
    public ValueTask<IVmConsole> RunProgramAsync(ProgramRunOptions options, CancellationToken cancellationToken) =>
        For(X86Permissions.ProgramsLaunch).RunProgramAsync(options, cancellationToken);

    /// <summary>
    /// The service of the calling app's user, once its permission is checked.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">No app calls, or it lacks <paramref name="permission"/>.</exception>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.InvalidUser"/>: the caller's user is not a Kazo user identifier.</exception>
    private X86Service For(string permission)
    {
        KazoFeatureCaller caller = _context.Caller
            ?? throw new UnauthorizedAccessException($"Os-x86 serves apps only: no app calls ('{permission}' is required).");
        caller.Require(permission);
        if (!X86Runtime.TryParseUserId(caller.UserId, out Guid userId))
        {
            throw new X86Exception(X86ErrorCodes.InvalidUser, $"The user of {caller.AppId} is not a Kazo user identifier.");
        }

        return new X86Service(_manager(), userId);
    }

    private sealed record Subscription(EventHandler<VmStateChangedEventArgs> Handler, X86Service User, EventHandler<VmStateChangedEventArgs> Relay);
}
