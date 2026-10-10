using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using N = MeetingCompanion.Platform.WindowsInventoryNative;

namespace MeetingCompanion.Platform;

/// <summary>Toolhelp + process creation identity + WASAPI session metadata only. Never starts an audio client.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCallSourceInventory : ICallSourceInventory
{
    private readonly object gate = new();
    private Task<CallSourceInventory>? inFlight;
    public async Task<CallSourceInventory> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CallSourceInventory> task;
        lock (gate)
        {
            // A blocked OS call cannot be canceled. Permit only one outstanding worker per instance.
            if (inFlight is null || inFlight.IsCompleted)
                inFlight = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            task = inFlight;
        }
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new(Environment.OSVersion.Version.Build, Supported(), [], [], [], ["inventory_timeout"], false);
        }
    }
    private static bool Supported() => Environment.Is64BitProcess && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    public IProcessIdentityLease Pin(ProcessIdentity identity)
    {
        var handle = N.OpenProcess(0x00100000 | 0x1000, false, identity.ProcessId);
        try
        {
            if (handle.IsInvalid) throw new CallSourceSelectionException("selected_process_unavailable");
            if (ReadIdentity(handle, identity.ProcessId) != identity)
                throw new CallSourceSelectionException("process_identity_changed");
            if (N.WaitForSingleObject(handle, 0) != 258)
                throw new CallSourceSelectionException("selected_process_exited");
            return new Lease(handle);
        }
        catch (Win32Exception e)
        {
            handle.Dispose();
            throw new CallSourceSelectionException($"process_metadata_unavailable_{e.NativeErrorCode}");
        }
        catch { handle.Dispose(); throw; }
    }
    private sealed class Lease(SafeWaitHandle handle) : IProcessIdentityLease
    {
        public bool IsAlive => !handle.IsClosed && !handle.IsInvalid && N.WaitForSingleObject(handle, 0) == 258;
        public void Dispose() => handle.Dispose();
    }
    private static ProcessIdentity ReadIdentity(SafeWaitHandle handle, int pid)
    {
        if (!N.GetProcessTimes(handle, out var creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768);
        uint size = (uint)path.Capacity;
        if (!N.QueryFullProcessImageNameW(handle, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(pid, creation, path.ToString());
    }
    private static ProcessObservation[] Processes()
    {
        using var snapshot = N.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
        if (!N.Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var rows = new List<ProcessObservation>();
        do
        {
            if (entry.ProcessId == 0 || entry.ProcessId > int.MaxValue) continue;
            var pid = (int)entry.ProcessId;
            ProcessIdentity? identity = null;
            string? diagnostic = null;
            using var handle = N.OpenProcess(0x00100000 | 0x1000, false, pid);
            try
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                identity = ReadIdentity(handle, pid);
                // Metadata and topology must describe the same live executable observed by Toolhelp.
                if (N.WaitForSingleObject(handle, 0) != 258 || !Path.GetFileName(identity.ExecutablePath).Equals(entry.Exe, StringComparison.OrdinalIgnoreCase))
                { identity = null; diagnostic = "process_changed_during_inventory"; }
            }
            catch (Win32Exception error) { diagnostic = $"process_metadata_unavailable_{error.NativeErrorCode}"; }
            rows.Add(new(pid, checked((int)entry.ParentProcessId), entry.Exe, identity, null, diagnostic));
        } while (N.Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        return rows.ToArray();
    }
    private static CallSourceInventory Read()
    {
        List<string> diagnostics = [];
        ProcessObservation[] first;
        try { first = Processes(); }
        catch (Win32Exception e) { return new(Environment.OSVersion.Version.Build, Supported(), [], [], [], [$"process_inventory_failed_{e.NativeErrorCode}"], false); }
        List<AudioEndpoint> endpoints = [];
        List<RenderSession> sessions = [];
        IMMDeviceEnumerator? enumerator = null;
        var initialized = false;
        try
        {
            N.Check(N.CoInitializeEx(0, 0)); initialized = true;
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            for (int flow = 0; flow < 2; flow++)
            {
                IMMDeviceCollection? devices = null;
                try
                {
                    N.Check(enumerator.EnumAudioEndpoints(flow, 1, out devices));
                    N.Check(devices.GetCount(out uint count));
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice? device = null;
                        try
                        {
                            N.Check(devices.Item(i, out device));
                            N.Check(device.GetId(out var id));
                            endpoints.Add(new(id, Name(device), flow == 1));
                            if (flow == 0) ReadSessions(device, id, first, sessions, diagnostics);
                        }
                        catch (COMException e) { diagnostics.Add($"endpoint_inventory_failed_{e.HResult:X8}"); }
                        finally { N.Release(device); }
                    }
                }
                catch (COMException e) { diagnostics.Add($"audio_inventory_failed_{e.HResult:X8}"); }
                finally { N.Release(devices); }
            }
        }
        catch (COMException e) { diagnostics.Add($"audio_inventory_failed_{e.HResult:X8}"); }
        finally { N.Release(enumerator); if (initialized) N.CoUninitialize(); }
        ProcessObservation[] second;
        try { second = Processes(); }
        catch (Win32Exception e) { return new(Environment.OSVersion.Version.Build, Supported(), [], endpoints.ToArray(), [], [$"process_inventory_failed_{e.NativeErrorCode}"], false); }
        var stable = first.Where(a => second.Any(b => a.ProcessId == b.ProcessId && a.Identity == b.Identity &&
            a.ParentProcessId == b.ParentProcessId && a.ExecutableName == b.ExecutableName)).ToArray();
        var unstable = first.Concat(second).Where(p => !stable.Any(s => s.ProcessId == p.ProcessId))
            .GroupBy(p => p.ProcessId).Select(g => g.Last() with { Identity = null, DiagnosticCode = "process_changed_during_inventory" }).ToArray();
        var windows = new Dictionary<int, long>();
        N.EnumWindows((window, _) =>
        {
            N.GetWindowThreadProcessId(window, out var pid);
            if (pid <= int.MaxValue && N.IsWindowVisible(window)) windows.TryAdd((int)pid, window.ToInt64());
            return true;
        }, 0);
        stable = stable.Select(p => p with { WindowHandle = windows.TryGetValue(p.ProcessId, out var w) ? w : null }).ToArray();
        sessions = sessions.Where(s => s.Process is null || stable.Any(p => p.Identity == s.Process)).ToList();
        return new(Environment.OSVersion.Version.Build, Supported(), [.. stable, .. unstable], endpoints.ToArray(), sessions.ToArray(), diagnostics.Distinct().ToArray());
    }
    private static string Name(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            N.Check(device.OpenPropertyStore(0, out store));
            var key = new PropertyKey { Format = new("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            N.Check(store.GetValue(key, out var value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "Audio device" : "Audio device"; }
            finally { N.PropVariantClear(ref value); }
        }
        finally { N.Release(store); }
    }
    private static void ReadSessions(IMMDevice device, string endpoint, ProcessObservation[] processes,
        List<RenderSession> result, List<string> diagnostics)
    {
        object? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            N.Check(device.Activate(N.SessionManagerId, 23, 0, out manager));
            N.Check(((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions));
            N.Check(sessions.GetCount(out int count));
            for (int i = 0; i < count; i++)
            {
                object? control = null;
                try
                {
                    N.Check(sessions.GetSession(i, out control));
                    var session = (IAudioSessionControl2)control;
                    N.Check(session.GetSessionInstanceIdentifier(out var id));
                    N.Check(session.GetState(out int state));
                    int hr = session.GetProcessId(out uint pid); N.Check(hr);
                    int system = session.IsSystemSoundsSession(); N.Check(system);
                    var process = processes.SingleOrDefault(p => p.ProcessId == pid)?.Identity;
                    if (process is null && system != 0 && state != 2) diagnostics.Add("audio_session_process_unavailable");
                    result.Add(new(endpoint, id, process, (RenderSessionState)state, hr == 0x0889000D, system == 0));
                }
                catch (COMException e) { diagnostics.Add($"session_inventory_failed_{e.HResult:X8}"); }
                finally { N.Release(control); }
            }
        }
        finally { N.Release(sessions); N.Release(manager); }
    }
}
