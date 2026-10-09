using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

namespace MeetingCompanion.Unit.Tests;

public sealed class SessionStateMachineTests
{
    private static SessionStateMachine Starting()
    {
        var machine = new SessionStateMachine();
        machine.Dispatch(SessionSignal.SelectSource, 0);
        machine.Dispatch(SessionSignal.SourcesSelected, 0);
        machine.Dispatch(SessionSignal.Start, 0);
        return machine;
    }

    private static void Healthy(SessionStateMachine machine, long now)
    {
        foreach (var component in new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt })
            machine.ReportHealth(new(component, HealthStatus.Healthy, now, null), now);
    }

    [Fact]
    public void StartWithoutHealthIsDegraded()
    {
        Assert.Equal(SessionState.Degraded, Starting().Dispatch(SessionSignal.Started, 1).State);
    }

    [Theory]
    [InlineData(Component.LocalCapture)]
    [InlineData(Component.RemoteCapture)]
    [InlineData(Component.LocalStt)]
    [InlineData(Component.RemoteStt)]
    public void FailureOfAnyRequiredComponentDegrades(Component component)
    {
        var machine = Starting(); Healthy(machine, 1);
        Assert.Equal(SessionState.Listening, machine.Dispatch(SessionSignal.Started, 1).State);
        Assert.Equal(SessionState.Degraded, machine.ReportHealth(new(component, HealthStatus.Disconnected, 2, "disconnected"), 2).State);
        machine.Dispatch(SessionSignal.Recover, 3);
        Assert.Equal(SessionState.Degraded, machine.Dispatch(SessionSignal.Recovered, 4).State);
        machine.Dispatch(SessionSignal.Recover, 5); Healthy(machine, 6);
        Assert.Equal(SessionState.Listening, machine.Dispatch(SessionSignal.Recovered, 6).State);
    }

    [Fact]
    public void ReasoningFailureDoesNotInterruptTranscription()
    {
        var machine = Starting(); Healthy(machine, 1);
        machine.Dispatch(SessionSignal.Started, 1);
        var result = machine.ReportHealth(new(Component.Reasoning, HealthStatus.Failed, 2, "unavailable"), 2);
        Assert.Equal(SessionState.Listening, result.State);
        Assert.True(result.CanSendAudio);
        Assert.False(result.CanGenerate);
    }

    [Fact]
    public void ExpiredHealthRequiresExplicitRecovery()
    {
        var machine = Starting(); Healthy(machine, 1); machine.Dispatch(SessionSignal.Started, 1);
        Assert.Equal(SessionState.Degraded, machine.Tick(2001).State);
        Healthy(machine, 2002);
        Assert.Equal(SessionState.Degraded, machine.Tick(2002).State);
        machine.Dispatch(SessionSignal.Recover, 2003); Healthy(machine, 2004);
        Assert.Equal(SessionState.Listening, machine.Dispatch(SessionSignal.Recovered, 2004).State);
    }

    [Fact]
    public void PauseStopsOutboundWorkAndResumeRequiresFreshHealth()
    {
        var machine = Starting(); Healthy(machine, 1); machine.Dispatch(SessionSignal.Started, 1);
        machine.ReportHealth(new(Component.Reasoning, HealthStatus.Healthy, 2, null), 2);
        Assert.True(machine.Tick(2).CanGenerate);
        var paused = machine.Dispatch(SessionSignal.Pause, 3);
        Assert.False(paused.CanSendAudio); Assert.False(paused.CanGenerate);
        machine.ReportHealth(new(Component.LocalCapture, HealthStatus.Healthy, 4, null), 4);
        machine.Dispatch(SessionSignal.Resume, 5);
        Assert.Empty(machine.Tick(5).Health);
        Assert.Equal(SessionState.Degraded, machine.Dispatch(SessionSignal.Recovered, 6).State);
    }

    [Fact]
    public void StaleAndFutureHealthCannotPromoteState()
    {
        var machine = Starting(); Healthy(machine, 10);
        machine.ReportHealth(new(Component.LocalCapture, HealthStatus.Failed, 9, null), 10);
        Assert.Equal(SessionState.Listening, machine.Dispatch(SessionSignal.Started, 10).State);
        Assert.Throws<ArgumentException>(() => machine.ReportHealth(new(Component.LocalCapture, HealthStatus.Healthy, 12, null), 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.Tick(9));
    }

    [Theory]
    [InlineData(SessionSignal.Stop)]
    [InlineData(SessionSignal.Cancel)]
    public void StopOrCancelFromStartingDisablesWorkUntilCleanupAcknowledged(SessionSignal signal)
    {
        var machine = Starting();
        var stopping = machine.Dispatch(signal, 1);
        Assert.Equal(SessionState.Stopping, stopping.State);
        Assert.False(stopping.CanSendAudio); Assert.False(stopping.CanGenerate);
        Assert.Equal(SessionState.Stopped, machine.Dispatch(SessionSignal.Stopped, 2).State);
        Assert.Empty(machine.Tick(2).Health);
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Recovering)]
    [InlineData(SessionState.Stopping)]
    public void OperationTimeoutEntersErrorAndAllowsExplicitRetry(SessionState state)
    {
        var machine = Starting();
        if (state == SessionState.Recovering)
        {
            machine.Dispatch(SessionSignal.Started, 0);
            machine.Dispatch(SessionSignal.Recover, 0);
        }
        if (state == SessionState.Stopping) machine.Dispatch(SessionSignal.Stop, 0);
        var failed = machine.Tick(10_000);
        Assert.Equal(SessionState.Error, failed.State); Assert.False(failed.CanSendAudio);
        Assert.Equal(SessionState.SelectingSource, machine.Dispatch(SessionSignal.Retry, 10_001).State);
    }

    [Fact]
    public void InvalidTransitionsNeverEnableListening()
    {
        var machine = new SessionStateMachine();
        foreach (var signal in new[] { SessionSignal.Start, SessionSignal.Started, SessionSignal.Resume, SessionSignal.Recovered })
            Assert.Throws<InvalidOperationException>(() => machine.Dispatch(signal, 0));
        Assert.Equal(SessionState.Stopped, machine.Tick(0).State);
    }

    [Fact]
    public void HundredStartStopCyclesClearHealth()
    {
        var machine = new SessionStateMachine();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            long now = cycle * 10;
            machine.Dispatch(SessionSignal.SelectSource, now);
            machine.Dispatch(SessionSignal.SourcesSelected, now);
            machine.Dispatch(SessionSignal.Start, now);
            Healthy(machine, now + 1); machine.Dispatch(SessionSignal.Started, now + 1);
            machine.Dispatch(SessionSignal.Stop, now + 2);
            var stopped = machine.Dispatch(SessionSignal.Stopped, now + 3);
            Assert.Equal(SessionState.Stopped, stopped.State); Assert.Empty(stopped.Health);
            Assert.Empty(machine.ReportHealth(new(Component.LocalCapture, HealthStatus.Healthy, now + 4, null), now + 4).Health);
        }
    }

    [Theory]
    [InlineData(HealthStatus.Silence, SessionState.Listening)]
    [InlineData(HealthStatus.ZeroLevel, SessionState.Degraded)]
    [InlineData(HealthStatus.WrongProcess, SessionState.Degraded)]
    [InlineData(HealthStatus.NoDevice, SessionState.Degraded)]
    [InlineData(HealthStatus.PermissionDenied, SessionState.Degraded)]
    public void VerifiedSilenceAndFailedSourcesHaveDifferentReadiness(HealthStatus status, SessionState expected)
    {
        var machine = Starting(); Healthy(machine, 1);
        machine.ReportHealth(new(Component.RemoteCapture, status, 2, null), 2);
        Assert.Equal(expected, machine.Dispatch(SessionSignal.Started, 2).State);
    }

    [Fact]
    public void FailDisablesWorkAndStopRequiresCleanup()
    {
        var machine = Starting(); Healthy(machine, 1); machine.Dispatch(SessionSignal.Started, 1);
        var failed = machine.Dispatch(SessionSignal.Fail, 2);
        Assert.Equal(SessionState.Error, failed.State); Assert.False(failed.CanSendAudio); Assert.False(failed.CanGenerate);
        Assert.Equal(SessionState.Stopping, machine.Dispatch(SessionSignal.Stop, 3).State);
        Assert.Equal(SessionState.Stopped, machine.Dispatch(SessionSignal.Stopped, 4).State);
        Assert.Empty(machine.Tick(4).Health);
    }

    [Fact]
    public void CancelBeforeCaptureAndRepeatedStopAreIdempotent()
    {
        var machine = new SessionStateMachine();
        machine.Dispatch(SessionSignal.SelectSource, 0);
        Assert.Equal(SessionState.Stopped, machine.Dispatch(SessionSignal.Cancel, 1).State);
        Assert.Equal(SessionState.Stopped, machine.Dispatch(SessionSignal.Stop, 2).State);
        machine.Dispatch(SessionSignal.SelectSource, 3);
        machine.Dispatch(SessionSignal.SourcesSelected, 4);
        machine.Dispatch(SessionSignal.Start, 5);
        machine.Dispatch(SessionSignal.Stop, 6);
        Assert.Equal(SessionState.Stopping, machine.Dispatch(SessionSignal.Stop, 7).State);
    }
}
