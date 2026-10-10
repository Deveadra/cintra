using System.Runtime.InteropServices;

namespace MeetingCompanion.Capture;

public interface IHotkeyApi
{
    bool Register(nint window, int id, uint modifiers, uint key);
    void Unregister(nint window, int id);
}
public sealed class WindowsHotkeyApi : IHotkeyApi
{
    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")] private static extern bool UnregisterHotKey(nint window, int id);
    public bool Register(nint window, int id, uint modifiers, uint key) => RegisterHotKey(window, id, modifiers | 0x4000, key);
    public void Unregister(nint window, int id) => UnregisterHotKey(window, id);
}
public sealed class SnapshotHotkey(IHotkeyApi api, nint window) : IDisposable
{
    private const int Id = 0x4C08;
    private bool registered;
    public bool Rebind(uint modifiers, uint key)
    {
        if (registered) api.Unregister(window, Id);
        registered = api.Register(window, Id, modifiers, key);
        return registered;
    }
    public bool IsMessage(int message, nint wParam) => registered && message == 0x312 && wParam == Id;
    public void Dispose() { if (registered) api.Unregister(window, Id); registered = false; }
}
