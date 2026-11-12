namespace OsX86.Engine;

/// <summary>The operations a caller can request from an engine.</summary>
public enum EngineOperation
{
    /// <summary>Boot an image (<see cref="EngineState.Stopped"/> to <see cref="EngineState.Running"/>).</summary>
    Start = 0,

    /// <summary>Tear the emulator down (to <see cref="EngineState.Stopped"/>).</summary>
    Stop = 1,

    /// <summary>Suspend execution (<see cref="EngineState.Running"/> to <see cref="EngineState.Paused"/>).</summary>
    Pause = 2,

    /// <summary>Continue execution (<see cref="EngineState.Paused"/> to <see cref="EngineState.Running"/>).</summary>
    Resume = 3,

    /// <summary>Send bytes to a console port (allowed while starting, running or paused).</summary>
    Write = 4,
}

/// <summary>
/// The rules of <see cref="EngineState"/>: which operation is allowed in which state, what state
/// each operation goes through and ends in, and which reports from the worker are legal. Pure
/// functions, so the engine implementations and the tests share one definition.
/// </summary>
public static class EngineStateMachine
{
    /// <summary>Whether <paramref name="operation"/> may be requested while the engine is in <paramref name="state"/>.</summary>
    public static bool CanRequest(EngineState state, EngineOperation operation) => operation switch
    {
        EngineOperation.Start => state == EngineState.Stopped,
        EngineOperation.Stop => state is EngineState.Starting or EngineState.Running or EngineState.Paused,
        EngineOperation.Pause => state == EngineState.Running,
        EngineOperation.Resume => state == EngineState.Paused,
        EngineOperation.Write => state is EngineState.Starting or EngineState.Running or EngineState.Paused,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="operation"/> is already satisfied (or under way) in <paramref name="state"/>,
    /// so requesting it again is a harmless no-op rather than an error: stopping a stopped engine.
    /// </summary>
    public static bool IsNoOp(EngineState state, EngineOperation operation) =>
        operation == EngineOperation.Stop && state is EngineState.Stopped or EngineState.Stopping;

    /// <summary>The state the engine enters as soon as the operation is requested (lifecycle operations only).</summary>
    public static EngineState TransientState(EngineOperation operation) => operation switch
    {
        EngineOperation.Start => EngineState.Starting,
        EngineOperation.Stop => EngineState.Stopping,
        EngineOperation.Pause => EngineState.Running,
        EngineOperation.Resume => EngineState.Paused,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>The state that completes the operation (lifecycle operations only).</summary>
    public static EngineState TargetState(EngineOperation operation) => operation switch
    {
        EngineOperation.Start => EngineState.Running,
        EngineOperation.Stop => EngineState.Stopped,
        EngineOperation.Pause => EngineState.Paused,
        EngineOperation.Resume => EngineState.Running,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>
    /// Whether the worker may report <paramref name="reported"/> while the host believes the engine is
    /// in <paramref name="current"/>. Reporting the same state again is always legal (queries,
    /// idempotent stops); <see cref="EngineState.Stopped"/> is reachable from everywhere (crash, fatal error).
    /// </summary>
    public static bool IsLegalReport(EngineState current, EngineState reported)
    {
        if (current == reported || reported == EngineState.Stopped)
        {
            return true;
        }

        return (current, reported) switch
        {
            (EngineState.Stopped, EngineState.Starting) => true,
            (EngineState.Starting, EngineState.Running) => true,
            (EngineState.Starting, EngineState.Stopping) => true,
            (EngineState.Running, EngineState.Paused) => true,
            (EngineState.Running, EngineState.Stopping) => true,
            (EngineState.Paused, EngineState.Running) => true,
            (EngineState.Paused, EngineState.Stopping) => true,
            _ => false,
        };
    }

    /// <summary>Parses the state names of the worker protocol (<c>stopped</c>, <c>starting</c>, ...).</summary>
    public static bool TryParse(string? name, out EngineState state)
    {
        switch (name)
        {
            case "stopped":
                state = EngineState.Stopped;
                return true;
            case "starting":
                state = EngineState.Starting;
                return true;
            case "running":
                state = EngineState.Running;
                return true;
            case "paused":
                state = EngineState.Paused;
                return true;
            case "stopping":
                state = EngineState.Stopping;
                return true;
            default:
                state = EngineState.Stopped;
                return false;
        }
    }
}
