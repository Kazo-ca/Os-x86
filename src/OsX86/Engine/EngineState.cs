namespace OsX86.Engine;

/// <summary>
/// The execution state of an engine (milestone 0, work item 2): what the worker that runs the
/// emulator reports, mirrored by <see cref="IEngine.State"/>. The VM lifecycle of the plan
/// (<see cref="Interface.VmState"/>) is built on top of it by the VM registry (work item 6).
/// </summary>
public enum EngineState
{
    /// <summary>No emulator: nothing runs, no image is open.</summary>
    Stopped = 0,

    /// <summary>The image is being read and the emulator created; the guest has not started yet.</summary>
    Starting = 1,

    /// <summary>The emulator executes the guest.</summary>
    Running = 2,

    /// <summary>The emulator keeps the guest in memory but does not execute it.</summary>
    Paused = 3,

    /// <summary>The emulator is being torn down; <see cref="Stopped"/> follows.</summary>
    Stopping = 4,
}
