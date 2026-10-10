using System.Runtime.InteropServices;
using System.Text;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Capture;

public sealed record MonitorSource(nint Handle, string Label, PixelRegion Bounds)
{
    public override string ToString() => Label;
}
public sealed record WindowSource(nint Handle, string Label)
{
    public override string ToString() => Label;
}
public static class WindowsSources
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(nint window, nint state);
    private delegate bool EnumMonitor(nint monitor, nint dc, ref Rect rect, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitor callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    public static bool IsExternal(nint window)
    {
        if (window == 0 || !IsWindow(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid != Environment.ProcessId;
    }
    public static string Title(nint window)
    {
        var text = new StringBuilder(257);
        GetWindowText(window, text, text.Capacity);
        return text.Length == 0 ? "Untitled window" : text.ToString();
    }
    public static IReadOnlyList<WindowSource> Windows()
    {
        var result = new List<WindowSource>();
        EnumWindows((window, _) =>
        {
            if (IsExternal(window) && IsWindowVisible(window) && !IsIconic(window)) result.Add(new(window, Title(window)));
            return true;
        }, 0);
        return result;
    }
    public static IReadOnlyList<MonitorSource> Monitors()
    {
        var result = new List<MonitorSource>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect rect, nint state) =>
        {
            result.Add(new(monitor, $"Monitor {result.Count + 1} — {rect.Right - rect.Left}×{rect.Bottom - rect.Top} at ({rect.Left},{rect.Top})",
                new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        }, 0);
        return result;
    }
    public static bool Exclude(nint window) => SetWindowDisplayAffinity(window, 0x11) && GetWindowDisplayAffinity(window, out uint affinity) && affinity == 0x11;
}
/// <summary>Tracks HWND before companion UI takes focus; never captures pixels.</summary>
public sealed class ForegroundTracker : IDisposable
{
    private delegate void WinEvent(nint hook, uint evt, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    private readonly WinEvent callback;
    private readonly nint hook;
    public nint LastWindow { get; private set; }
    public ForegroundTracker()
    {
        Remember(WindowsSources.GetForegroundWindow());
        callback = (_, _, window, _, _, _, _) => Remember(window);
        hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 2);
        if (hook == 0) throw new SnapshotFailure("foreground_hook", "Windows could not track the active window. Use selected window capture.");
    }
    private void Remember(nint window) { if (WindowsSources.IsExternal(window)) LastWindow = window; }
    public void Dispose() { if (hook != 0) UnhookWinEvent(hook); GC.KeepAlive(callback); }
}
