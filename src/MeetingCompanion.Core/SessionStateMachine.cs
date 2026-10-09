using MeetingCompanion.Contracts;

namespace MeetingCompanion.Core;

public enum SessionSignal { SelectSource, SourcesSelected, Start, Started, Pause, Resume, Recover, Recovered, Stop, Stopped, Cancel, Fail, Retry }
public sealed record SessionSnapshot(SessionState State, ComponentHealth[] Health, bool CanSendAudio, bool CanGenerate);

/// <summary>
/// Serialized, deterministic policy only. The orchestrator must perform side effects before acknowledging
/// Started/Recovered/Stopped. Time is session-relative monotonic milliseconds supplied by its owner.
/// </summary>
public sealed class SessionStateMachine
{
    private static readonly Component[] RequiredComponents =
        [Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt];
    private readonly object gate = new();
    private readonly Dictionary<Component, ComponentHealth> health = [];
    private readonly long healthTtlMs;
    private readonly long operationTimeoutMs;
    private SessionState state = SessionState.Stopped;
    private long lastTimeMs;
    private long enteredAtMs;

    public SessionStateMachine(long healthTtlMs = 2000, long operationTimeoutMs = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(healthTtlMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationTimeoutMs);
        this.healthTtlMs = healthTtlMs;
        this.operationTimeoutMs = operationTimeoutMs;
    }

    public SessionSnapshot Dispatch(SessionSignal signal, long nowMs)
    {
        lock (gate)
        {
            Advance(nowMs);
            var next = (state, signal) switch
            {
                (SessionState.Stopped, SessionSignal.SelectSource) => SessionState.SelectingSource,
                (SessionState.SelectingSource, SessionSignal.SourcesSelected) => SessionState.Ready,
                (SessionState.Ready, SessionSignal.Start) => SessionState.Starting,
                (SessionState.Starting, SessionSignal.Started) => Healthy(nowMs) ? SessionState.Listening : SessionState.Degraded,
                (SessionState.Listening or SessionState.Degraded or SessionState.Recovering, SessionSignal.Pause) => SessionState.Paused,
                (SessionState.Paused, SessionSignal.Resume) => SessionState.Recovering,
                (SessionState.Degraded, SessionSignal.Recover) => SessionState.Recovering,
                (SessionState.Recovering, SessionSignal.Recovered) => Healthy(nowMs) ? SessionState.Listening : SessionState.Degraded,
                (SessionState.Error, SessionSignal.Retry) => SessionState.SelectingSource,
                (SessionState.SelectingSource or SessionState.Ready, SessionSignal.Cancel) => SessionState.Stopped,
                (SessionState.Stopping, SessionSignal.Stopped) => SessionState.Stopped,
                (SessionState.Stopped, SessionSignal.Stop or SessionSignal.Stopped) => SessionState.Stopped,
                (SessionState.Stopping, SessionSignal.Stop or SessionSignal.Cancel) => SessionState.Stopping,
                (_, SessionSignal.Stop or SessionSignal.Cancel) when state != SessionState.Stopped => SessionState.Stopping,
                (_, SessionSignal.Fail) when state != SessionState.Stopped => SessionState.Error,
                _ => throw new InvalidOperationException($"Invalid session transition: {state} / {signal}.")
            };
            if (signal is SessionSignal.Start or SessionSignal.Resume or SessionSignal.Recover or SessionSignal.Retry)
                health.Clear(); // A previous operation's heartbeat cannot establish recovery.
            SetState(next, nowMs);
            if (state == SessionState.Stopped) health.Clear();
            return Snapshot(nowMs);
        }
    }

    public SessionSnapshot ReportHealth(ComponentHealth report, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Validate();
        lock (gate)
        {
            Advance(nowMs);
            if (report.ObservedAtMs > nowMs) throw new ArgumentException("Health cannot come from the future.", nameof(report));
            if (state is SessionState.Stopped or SessionState.Stopping or SessionState.Error or SessionState.Ready or SessionState.SelectingSource or SessionState.Paused)
                return Snapshot(nowMs);
            if (report.ObservedAtMs < enteredAtMs ||
                (health.TryGetValue(report.Component, out var previous) && report.ObservedAtMs <= previous.ObservedAtMs))
                return Snapshot(nowMs);
            health[report.Component] = report;
            if (state == SessionState.Listening && !Healthy(nowMs)) SetState(SessionState.Degraded, nowMs);
            return Snapshot(nowMs);
        }
    }

    /// <summary>Call periodically even during silence; expiration can never promote a session to Listening.</summary>
    public SessionSnapshot Tick(long nowMs)
    {
        lock (gate) { Advance(nowMs); return Snapshot(nowMs); }
    }

    private void Advance(long nowMs)
    {
        if (nowMs < lastTimeMs) throw new ArgumentOutOfRangeException(nameof(nowMs), "Monotonic time cannot go backwards.");
        lastTimeMs = nowMs;
        if (state == SessionState.Listening && !Healthy(nowMs)) SetState(SessionState.Degraded, nowMs);
        if (state is SessionState.Starting or SessionState.Recovering or SessionState.Stopping && nowMs - enteredAtMs >= operationTimeoutMs)
            SetState(SessionState.Error, nowMs);
    }

    private bool Healthy(long nowMs) => RequiredComponents.All(component =>
        health.TryGetValue(component, out var value) &&
        value.Status is HealthStatus.Healthy or HealthStatus.Silence &&
        nowMs - value.ObservedAtMs < healthTtlMs);

    private void SetState(SessionState next, long nowMs)
    {
        if (state == next) return;
        state = next;
        enteredAtMs = nowMs;
    }

    private SessionSnapshot Snapshot(long nowMs)
    {
        var active = state is SessionState.Starting or SessionState.Listening or SessionState.Degraded or SessionState.Recovering;
        var reasoningHealthy = health.TryGetValue(Component.Reasoning, out var reasoning) &&
            reasoning.Status == HealthStatus.Healthy && nowMs - reasoning.ObservedAtMs < healthTtlMs;
        return new(state, health.Values.OrderBy(x => x.Component).ToArray(), active, active && reasoningHealthy);
    }
}
