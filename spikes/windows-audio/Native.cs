using System.Runtime.InteropServices;

namespace WindowsAudioSpike;

// Standalone ABI declarations. No dependency on production contracts or packages.
internal static class Native
{
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    internal static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    internal static readonly Guid RenderClientId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal static readonly Guid SessionManagerId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    internal static readonly Guid PcmId = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid FloatId = new("00000003-0000-0010-8000-00aa00389b71");
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropVariant value);
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ActivateAudioInterfaceAsync(string path, in Guid iid,
        in PropVariant parameters, IActivateCompletion completion, out IActivateOperation operation);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
    internal static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ProcessEntry
{
    public uint Size, Usage, ProcessId;
    public nuint DefaultHeap;
    public uint ModuleId, Threads, ParentProcessId;
    public int Priority;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
}
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public nint Pointer;
    [FieldOffset(8)] public uint BlobSize;
    [FieldOffset(16)] public nint BlobData;
}
[StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort Tag, Channels;
    public uint Rate, BytesPerSecond;
    public ushort BlockAlign, Bits, ExtraSize;
}
[StructLayout(LayoutKind.Sequential)]
internal struct ActivationParameters { public int Type; public uint ProcessId; public int Mode; }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}
[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
}
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    void UnusedGetControl();
    void UnusedGetVolume();
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
}
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
}
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    void UnusedGetDisplay(); void UnusedSetDisplay(); void UnusedGetIcon(); void UnusedSetIcon();
    void UnusedGetGrouping(); void UnusedSetGrouping(); void UnusedRegister(); void UnusedUnregister();
    void UnusedGetIdentifier(); void UnusedGetInstanceIdentifier();
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
}
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long periodicity, nint format, nint sessionId);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
    [PreserveSig] int GetMixFormat(out nint format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpc100ns);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out nint data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}
[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateOperation
{
    [PreserveSig] int GetActivateResult(out int activationResult, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}
[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateCompletion
{
    [PreserveSig] int ActivateCompleted(IActivateOperation operation);
}
[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }

// Windows retains the CCW until completion. Parameters/event stay alive on timeout;
// a late completion releases its result rather than reviving an abandoned capture.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class Activation : IActivateCompletion, IAgileObject
{
    private readonly object gate = new();
    private readonly ManualResetEvent done = new(false);
    private object? result;
    private int hr = unchecked((int)0x80004005);
    private bool abandoned;
    private nint parameters;

    internal Activation(uint processId)
    {
        parameters = Marshal.AllocCoTaskMem(12);
        Marshal.StructureToPtr(new ActivationParameters { Type = 1, ProcessId = processId, Mode = 0 }, parameters, false);
    }
    internal PropVariant Blob => new() { Type = 65, BlobSize = 12, BlobData = parameters };
    public int ActivateCompleted(IActivateOperation operation)
    {
        object? obtained = null;
        try
        {
            int call = operation.GetActivateResult(out int activated, out obtained);
            lock (gate)
            {
                hr = call < 0 ? call : activated;
                if (!abandoned && hr >= 0) { result = obtained; obtained = null; }
            }
        }
        catch (Exception error) { lock (gate) hr = error.HResult; }
        finally
        {
            Native.Release(obtained);
            FreeParameters();
            done.Set();
        }
        return 0;
    }
    internal void FreeParameters()
    {
        nint pointer = Interlocked.Exchange(ref parameters, 0);
        if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
    }
    internal IAudioClient Wait(CancellationToken token)
    {
        int signaled = WaitHandle.WaitAny([done, token.WaitHandle], 5000);
        lock (gate)
        {
            if (signaled != 0 || token.IsCancellationRequested)
            {
                abandoned = true;
                Native.Release(result); result = null;
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("process activation did not complete within 5000 ms");
            }
            Native.Check(hr);
            object owned = result ?? throw new InvalidOperationException("activation returned no interface");
            result = null;
            try { return (IAudioClient)owned; }
            catch { Native.Release(owned); throw; }
        }
    }
}
