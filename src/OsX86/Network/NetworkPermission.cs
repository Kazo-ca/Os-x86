namespace OsX86.Network;

/// <summary>How long an answer of the permission window lasts.</summary>
public enum PermissionDuration
{
    /// <summary>This connection only.</summary>
    Once,

    /// <summary>Until the end of the session (kept in memory, never written).</summary>
    Session,

    /// <summary>Always: a rule is written.</summary>
    Always,
}

/// <summary>What the permission window asks: an identity wants to reach a destination no rule decides.</summary>
/// <param name="UserId">The user the VM belongs to (the window is shown to that user only).</param>
/// <param name="Identity">The VM, or the prefix of the VM, that connects.</param>
/// <param name="VmName">The VM's name, for the window.</param>
/// <param name="Host">The destination host.</param>
/// <param name="Port">The destination port.</param>
public sealed record NetworkPermissionRequest(Guid UserId, FlowIdentity Identity, string VmName, string Host, int Port);

/// <summary>The answer of the window.</summary>
/// <param name="Allow">Allow or refuse.</param>
/// <param name="Duration">How long it lasts.</param>
/// <param name="AnyDestination">Whether it covers any destination of the identity, or only this host and port.</param>
public sealed record NetworkPermissionAnswer(bool Allow, PermissionDuration Duration = PermissionDuration.Once, bool AnyDestination = false)
{
    /// <summary>"Allow", this connection only.</summary>
    public static NetworkPermissionAnswer AllowOnce { get; } = new(true);

    /// <summary>"Refuse", this connection only.</summary>
    public static NetworkPermissionAnswer DenyOnce { get; } = new(false);
}

/// <summary>
/// The permission window of Os-x86 (D23, D49; plan §1: one of the Feature's entry points), behind an
/// abstraction: the first connection no rule decides calls it. The window itself needs the
/// permission <c>network.real.access</c> of the Feature (ADR 0246 of Kazo), which is merged in Kazo
/// but not in a released version yet (Os-x86 is pinned on v3.4.0): until then the Feature has
/// <see cref="UnavailablePermissionPrompt"/>, and the tests inject their answers. Never a consent
/// typed in a terminal (D49): a guest program's output could imitate it.
/// </summary>
public interface INetworkPermissionPrompt
{
    /// <summary>Asks the user; <see langword="null"/> when no window can be shown (the connection is refused this time, nothing is remembered).</summary>
    ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken);
}

/// <summary>No window: every undecided connection is refused this time (journal reason <c>no-prompt</c>); rules still decide.</summary>
public sealed class UnavailablePermissionPrompt : INetworkPermissionPrompt
{
    /// <summary>The only instance.</summary>
    public static UnavailablePermissionPrompt Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NetworkPermissionAnswer?>(null);
}

/// <summary>
/// A window whose answers are given in advance (the tests, a command line that decides for the user
/// before running a program): each request takes the answer the function gives, and is kept in
/// <see cref="Requests"/> so a test sees that the window was opened.
/// </summary>
/// <param name="answer">The answer to a request, or <see langword="null"/> to show no window.</param>
public sealed class InjectedPermissionPrompt(Func<NetworkPermissionRequest, NetworkPermissionAnswer?> answer) : INetworkPermissionPrompt
{
    private readonly Lock _lock = new();
    private readonly List<NetworkPermissionRequest> _requests = [];

    /// <summary>The requests the window got, in order.</summary>
    public IReadOnlyList<NetworkPermissionRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<NetworkPermissionAnswer?> AskAsync(NetworkPermissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _requests.Add(request);
        }

        return ValueTask.FromResult(answer(request));
    }
}
