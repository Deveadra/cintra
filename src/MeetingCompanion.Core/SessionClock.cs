using MeetingCompanion.Contracts;

namespace MeetingCompanion.Core;

/// <summary>One immutable mapping shared by capture, STT and snapshot consumers for a session.</summary>
public sealed class SessionClock
{
    public ClockMapping Mapping { get; }

    public SessionClock(ClockMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Validate();
        Mapping = mapping;
    }

    public long FromMonotonicTicks(long ticks)
    {
        if (ticks < Mapping.MonotonicOriginTicks) throw new ArgumentOutOfRangeException(nameof(ticks));
        return checked((long)(((decimal)ticks - Mapping.MonotonicOriginTicks) * 1000 / Mapping.TicksPerSecond));
    }

    public DateTimeOffset ToUtc(long capturedAtMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capturedAtMs);
        return Mapping.UtcOrigin.AddMilliseconds(capturedAtMs);
    }

    public long FromUtc(DateTimeOffset utc) => Math.Max(0, checked((long)(utc - Mapping.UtcOrigin).TotalMilliseconds));
}
