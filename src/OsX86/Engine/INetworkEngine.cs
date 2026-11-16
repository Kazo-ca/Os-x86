namespace OsX86.Engine;

/// <summary>
/// An engine whose guest has a network card wired to the host (work item 12, decision 161 A): the
/// Ethernet frames the card sends come out through <see cref="FramesReceived"/>, the host's frames go
/// in through <see cref="SendFramesAsync"/>. Only when the engine was started with
/// <see cref="EngineStartOptions.Network"/>. The v86 engine is one.
/// </summary>
public interface INetworkEngine
{
    /// <summary>Raised with the frames the guest's card sent, in order (one batch per worker message).</summary>
    event EventHandler<NetworkFramesEventArgs>? FramesReceived;

    /// <summary>Hands frames to the guest's card, in order.</summary>
    /// <exception cref="EngineStateException">The engine is not running.</exception>
    ValueTask SendFramesAsync(IReadOnlyList<byte[]> frames, CancellationToken cancellationToken);
}

/// <summary>Arguments of <see cref="INetworkEngine.FramesReceived"/>.</summary>
/// <param name="frames">The frames, in order.</param>
public sealed class NetworkFramesEventArgs(IReadOnlyList<byte[]> frames) : EventArgs
{
    /// <summary>The frames, in order.</summary>
    public IReadOnlyList<byte[]> Frames { get; } = frames;
}
