using MeetingCompanion.Contracts;

namespace MeetingCompanion.Capture;

public static class RegionGeometry
{
    /// <summary>Physical desktop pixels to monitor-relative crop; supports negative monitor origins and reverse drags.</summary>
    public static PixelRegion? FromScreenPoints(PixelRegion monitor, double ax, double ay, double bx, double by)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0 || !double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(bx) || !double.IsFinite(by))
            throw new SnapshotFailure("invalid_region", "Invalid monitor geometry or selection.");
        long rightLimit = (long)monitor.X + monitor.Width;
        long bottomLimit = (long)monitor.Y + monitor.Height;
        long x = (long)Math.Clamp(Math.Floor(Math.Min(ax, bx)), monitor.X, rightLimit);
        long y = (long)Math.Clamp(Math.Floor(Math.Min(ay, by)), monitor.Y, bottomLimit);
        long right = (long)Math.Clamp(Math.Ceiling(Math.Max(ax, bx)), monitor.X, rightLimit);
        long bottom = (long)Math.Clamp(Math.Ceiling(Math.Max(ay, by)), monitor.Y, bottomLimit);
        return right > x && bottom > y ? new((int)(x - monitor.X), (int)(y - monitor.Y), (int)(right - x), (int)(bottom - y)) : null;
    }
}
