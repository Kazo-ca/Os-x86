using OsX86.Engine;

namespace OsX86.Tests.Engine;

/// <summary>The rules of <see cref="EngineStateMachine"/>.</summary>
[Trait("Category", "unit")]
public sealed class EngineStateMachineTests
{
    [Theory]
    [InlineData(EngineState.Stopped, EngineOperation.Start, true)]
    [InlineData(EngineState.Starting, EngineOperation.Start, false)]
    [InlineData(EngineState.Running, EngineOperation.Start, false)]
    [InlineData(EngineState.Paused, EngineOperation.Start, false)]
    [InlineData(EngineState.Stopping, EngineOperation.Start, false)]
    [InlineData(EngineState.Stopped, EngineOperation.Stop, false)]
    [InlineData(EngineState.Starting, EngineOperation.Stop, true)]
    [InlineData(EngineState.Running, EngineOperation.Stop, true)]
    [InlineData(EngineState.Paused, EngineOperation.Stop, true)]
    [InlineData(EngineState.Stopping, EngineOperation.Stop, false)]
    [InlineData(EngineState.Running, EngineOperation.Pause, true)]
    [InlineData(EngineState.Paused, EngineOperation.Pause, false)]
    [InlineData(EngineState.Starting, EngineOperation.Pause, false)]
    [InlineData(EngineState.Paused, EngineOperation.Resume, true)]
    [InlineData(EngineState.Running, EngineOperation.Resume, false)]
    [InlineData(EngineState.Stopped, EngineOperation.Resume, false)]
    [InlineData(EngineState.Starting, EngineOperation.Write, true)]
    [InlineData(EngineState.Running, EngineOperation.Write, true)]
    [InlineData(EngineState.Paused, EngineOperation.Write, true)]
    [InlineData(EngineState.Stopping, EngineOperation.Write, false)]
    [InlineData(EngineState.Stopped, EngineOperation.Write, false)]
    public void CanRequest_follows_the_lifecycle(EngineState state, EngineOperation operation, bool expected)
    {
        Assert.Equal(expected, EngineStateMachine.CanRequest(state, operation));
    }

    [Fact]
    public void CanRequest_refuses_an_unknown_operation()
    {
        Assert.False(EngineStateMachine.CanRequest(EngineState.Running, (EngineOperation)42));
    }

    [Theory]
    [InlineData(EngineState.Stopped, EngineOperation.Stop, true)]
    [InlineData(EngineState.Stopping, EngineOperation.Stop, true)]
    [InlineData(EngineState.Running, EngineOperation.Stop, false)]
    [InlineData(EngineState.Running, EngineOperation.Pause, false)]
    [InlineData(EngineState.Paused, EngineOperation.Pause, false)]
    public void IsNoOp_only_for_stopping_an_engine_that_is_already_stopping_or_stopped(EngineState state, EngineOperation operation, bool expected)
    {
        Assert.Equal(expected, EngineStateMachine.IsNoOp(state, operation));
    }

    [Theory]
    [InlineData(EngineOperation.Start, EngineState.Starting, EngineState.Running)]
    [InlineData(EngineOperation.Stop, EngineState.Stopping, EngineState.Stopped)]
    [InlineData(EngineOperation.Pause, EngineState.Running, EngineState.Paused)]
    [InlineData(EngineOperation.Resume, EngineState.Paused, EngineState.Running)]
    public void Each_operation_has_a_transient_and_a_target_state(EngineOperation operation, EngineState transient, EngineState target)
    {
        Assert.Equal(transient, EngineStateMachine.TransientState(operation));
        Assert.Equal(target, EngineStateMachine.TargetState(operation));
    }

    [Fact]
    public void Transient_and_target_states_reject_an_unknown_operation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineStateMachine.TransientState((EngineOperation)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineStateMachine.TargetState((EngineOperation)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineStateMachine.TransientState(EngineOperation.Write));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineStateMachine.TargetState(EngineOperation.Write));
    }

    [Theory]
    [InlineData(EngineState.Stopped, EngineState.Starting, true)]
    [InlineData(EngineState.Starting, EngineState.Running, true)]
    [InlineData(EngineState.Starting, EngineState.Stopped, true)]
    [InlineData(EngineState.Running, EngineState.Paused, true)]
    [InlineData(EngineState.Running, EngineState.Stopping, true)]
    [InlineData(EngineState.Running, EngineState.Stopped, true)]
    [InlineData(EngineState.Paused, EngineState.Running, true)]
    [InlineData(EngineState.Stopping, EngineState.Stopped, true)]
    [InlineData(EngineState.Running, EngineState.Running, true)]
    [InlineData(EngineState.Stopped, EngineState.Running, false)]
    [InlineData(EngineState.Stopped, EngineState.Paused, false)]
    [InlineData(EngineState.Paused, EngineState.Starting, false)]
    [InlineData(EngineState.Stopping, EngineState.Running, false)]
    public void IsLegalReport_accepts_the_worker_transitions_only(EngineState current, EngineState reported, bool expected)
    {
        Assert.Equal(expected, EngineStateMachine.IsLegalReport(current, reported));
    }

    [Theory]
    [InlineData("stopped", EngineState.Stopped)]
    [InlineData("starting", EngineState.Starting)]
    [InlineData("running", EngineState.Running)]
    [InlineData("paused", EngineState.Paused)]
    [InlineData("stopping", EngineState.Stopping)]
    public void TryParse_reads_the_protocol_names(string name, EngineState expected)
    {
        Assert.True(EngineStateMachine.TryParse(name, out EngineState state));
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Running")]
    [InlineData("crashed")]
    public void TryParse_rejects_anything_else(string? name)
    {
        Assert.False(EngineStateMachine.TryParse(name, out EngineState state));
        Assert.Equal(EngineState.Stopped, state);
    }
}
