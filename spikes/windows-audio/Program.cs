using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowsAudioSpike;

internal static class Program
{
    private static readonly object OutputGate = new();
    internal static void Emit(object item) { lock (OutputGate) Console.WriteLine(JsonSerializer.Serialize(item)); }
    internal static string Error(Exception error) => $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";

    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["self-test"])) return Tests.Run();
            if (args.Length > 0 && args[0] == "tone") return Tone.Run(args);
            Options options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("inventory | self-test | capture --pid <PID|self> [--mic <active-index>] --seconds <1..30> --acknowledge-capture\ncapture --mic <active-index> --seconds <1..30> --acknowledge-capture\nNo audio files, replay, network, or automatic source substitution. Ctrl+C stops capture.");
                return 0;
            }
            Native.Check(Native.CoInitializeEx(0, 0));
            try
            {
                if (!options.Capture) return Inventory.Run();
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
                ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                Console.CancelKeyPress += handler;
                try
                {
                    Emit(new { kind = "capture_notice", seconds = options.Seconds, microphoneIndex = options.Mic, targetProcessId = options.Pid, retention = "levels_only_no_audio", utc = DateTimeOffset.UtcNow, qpc = Stopwatch.GetTimestamp(), qpcFrequency = Stopwatch.Frequency });
                    var jobs = new List<Task<int>>();
                    if (options.Pid.HasValue) jobs.Add(Task.Run(() => Capture.Run("remote_app", options.Pid, null, cancel.Token)));
                    if (options.Mic.HasValue) jobs.Add(Task.Run(() => Capture.Run("local_mic", null, options.Mic, cancel.Token)));
                    Task.WaitAll(jobs.ToArray());
                    return jobs.Any(j => j.Result != 0) ? 2 : 0;
                }
                finally { Console.CancelKeyPress -= handler; }
            }
            finally { Native.CoUninitialize(); }
        }
        catch (Exception error) { Emit(new { kind = "error", detail = Error(error) }); return 2; }
    }
}

internal sealed record Options(bool Help, bool Capture, uint? Pid, uint? Mic, int Seconds)
{
    internal static Options Parse(string[] args)
    {
        if (args.Length == 0 || args.SequenceEqual(["--help"])) return new(true, false, null, null, 0);
        if (args.SequenceEqual(["inventory"])) return new(false, false, null, null, 0);
        if (args[0] != "capture") throw new ArgumentException("Expected inventory, self-test, or capture; see --help");
        uint? target = null, mic = null;
        int seconds = 0;
        bool acknowledged = false;
        var seen = new HashSet<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
            if (option == "--acknowledge-capture") { acknowledged = true; continue; }
            if (++i >= args.Length) throw new ArgumentException("Missing value: " + option);
            string value = args[i];
            switch (option)
            {
                case "--pid": target = value == "self" ? (uint)Environment.ProcessId : uint.Parse(value); break;
                case "--mic": mic = uint.Parse(value); break;
                case "--seconds": seconds = int.Parse(value); break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }
        if (!acknowledged || seconds is < 1 or > 30 || (target is null && mic is null) || target == 0 || target > int.MaxValue)
            throw new ArgumentException("Explicit sources, --seconds 1..30 and --acknowledge-capture required; PID must be positive Int32");
        return new(false, true, target, mic, seconds);
    }
}

internal static class Inventory
{
    internal static string Name(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            Native.Check(device.OpenPropertyStore(0, out store));
            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            Native.Check(store.GetValue(key, out var value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "unnamed" : "unnamed"; }
            finally { Native.PropVariantClear(ref value); }
        }
        finally { Native.Release(store); }
    }
    internal static List<ProcessEntry> Processes()
    {
        nint snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var rows = new List<ProcessEntry>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
            if (!Native.Process32FirstW(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            do { rows.Add(entry); } while (Native.Process32NextW(snapshot, ref entry));
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new System.ComponentModel.Win32Exception(error);
            return rows;
        }
        finally { Native.CloseHandle(snapshot); }
    }
    internal static int Run()
    {
        Program.Emit(new { kind = "environment", osVersion = Environment.OSVersion.Version.ToString(), x64 = Environment.Is64BitProcess, utc = DateTimeOffset.UtcNow });
        List<ProcessEntry> processes = [];
        int failures = 0;
        try
        {
            processes = Processes();
            var selected = processes.Where(p => p.Exe.Contains("zoom", StringComparison.OrdinalIgnoreCase) || p.Exe.Contains("teams", StringComparison.OrdinalIgnoreCase)).Select(p => p.ProcessId).ToHashSet();
            // Only observed descendants; a name is a discovery hint, never evidence of audio.
            bool added;
            do { added = false; foreach (var p in processes) if (selected.Contains(p.ParentProcessId)) added |= selected.Add(p.ProcessId); } while (added);
            foreach (var p in processes.Where(p => selected.Contains(p.ProcessId)))
                Program.Emit(new { kind = "candidate_process", pid = p.ProcessId, parent = p.ParentProcessId, executable = p.Exe, evidence = "name_or_descendant_only" });
            Program.Emit(new { kind = "candidate_count", count = selected.Count });
        }
        catch (Exception error) { failures++; Program.Emit(new { kind = "process_inventory_error", detail = Program.Error(error) }); }
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = Native.Enumerator();
            for (int flow = 0; flow <= 1; flow++)
            {
                IMMDeviceCollection? devices = null;
                try
                {
                    Native.Check(enumerator.EnumAudioEndpoints(flow, 1, out devices));
                    Native.Check(devices.GetCount(out uint count));
                    Program.Emit(new { kind = "endpoint_count", flow = flow == 0 ? "render" : "capture", count });
                    for (uint index = 0; index < count; index++)
                    {
                        IMMDevice? device = null;
                        try
                        {
                            Native.Check(devices.Item(index, out device));
                            Program.Emit(new { kind = "endpoint", flow = flow == 0 ? "render" : "capture", index, name = Name(device) });
                            if (flow == 0) Sessions(device, index, processes);
                        }
                        catch (Exception error) { failures++; Program.Emit(new { kind = "endpoint_error", flow, index, detail = Program.Error(error) }); }
                        finally { Native.Release(device); }
                    }
                }
                finally { Native.Release(devices); }
            }
        }
        catch (Exception error) { failures++; Program.Emit(new { kind = "audio_inventory_error", detail = Program.Error(error) }); }
        finally { Native.Release(enumerator); }
        return failures == 0 ? 0 : 2;
    }
    private static void Sessions(IMMDevice device, uint endpoint, List<ProcessEntry> processes)
    {
        object? managerObject = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            Native.Check(device.Activate(Native.SessionManagerId, 23, 0, out managerObject));
            Native.Check(((IAudioSessionManager2)managerObject).GetSessionEnumerator(out sessions));
            Native.Check(sessions.GetCount(out int count));
            Program.Emit(new { kind = "session_count", endpoint, count });
            for (int i = 0; i < count; i++)
            {
                object? control = null;
                try
                {
                    Native.Check(sessions.GetSession(i, out control));
                    var session = (IAudioSessionControl2)control;
                    int hr = session.GetProcessId(out uint pid); Native.Check(hr);
                    Native.Check(session.GetState(out int state));
                    var process = processes.FirstOrDefault(p => p.ProcessId == pid);
                    Program.Emit(new { kind = "audio_session", endpoint, pid, parent = process.ParentProcessId, executable = process.Exe ?? "unavailable", state, multiProcess = hr == 0x0889000D, systemSounds = session.IsSystemSoundsSession() == 0 });
                }
                finally { Native.Release(control); }
            }
        }
        finally { Native.Release(sessions); Native.Release(managerObject); }
    }
}

internal static class Capture
{
    internal static int Run(string source, uint? target, uint? mic, CancellationToken token)
    {
        bool com = false, started = false;
        object? clientObject = null, captureObject = null;
        IAudioClient? client = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        IMMDevice? device = null;
        nint formatPointer = 0;
        Process? targetProcess = null;
        using var ready = new AutoResetEvent(false);
        var totals = new Meter();
        var clock = Stopwatch.StartNew();
        long lastPacketMs = -1;
        int result = 0;
        try
        {
            Native.Check(Native.CoInitializeEx(0, 0)); com = true;
            token.ThrowIfCancellationRequested();
            WaveFormat format;
            if (target.HasValue)
            {
                targetProcess = Process.GetProcessById(checked((int)target.Value));
                // Hold a process handle: detect exit without accidentally following a reused PID.
                _ = targetProcess.Handle;
                if (targetProcess.HasExited) throw new InvalidOperationException("selected process already exited");
                Program.Emit(new { kind = "selected_process", source, pid = target, executable = targetProcess.ProcessName, startedUtc = targetProcess.StartTime.ToUniversalTime() });
                var activation = new Activation(target.Value);
                IActivateOperation? operation = null;
                try
                {
                    var parameters = activation.Blob;
                    int hr = Native.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", Native.AudioClientId, parameters, activation, out operation);
                    if (hr < 0) { activation.FreeParameters(); Native.Check(hr); }
                    client = activation.Wait(token); clientObject = client;
                }
                finally { Native.Release(operation); }
                format = new WaveFormat { Tag = 1, Channels = 2, Rate = 44100, Bits = 16, BlockAlign = 4, BytesPerSecond = 176400 };
                formatPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormat>());
                Marshal.StructureToPtr(format, formatPointer, false);
            }
            else
            {
                enumerator = Native.Enumerator();
                Native.Check(enumerator.EnumAudioEndpoints(1, 1, out devices));
                Native.Check(devices.GetCount(out uint count));
                if (mic is null || mic >= count) throw new InvalidOperationException($"microphone index unavailable; active count={count}");
                Native.Check(devices.Item(mic.Value, out device));
                Program.Emit(new { kind = "selected_microphone", source, index = mic, name = Inventory.Name(device) });
                Native.Check(device.Activate(Native.AudioClientId, 23, 0, out clientObject));
                client = (IAudioClient)clientObject;
                Native.Check(client.GetMixFormat(out formatPointer));
                format = Marshal.PtrToStructure<WaveFormat>(formatPointer);
            }
            bool floating = format.Tag == 3;
            if (format.Tag == 0xFFFE && format.ExtraSize >= 22)
            {
                var subtype = Marshal.PtrToStructure<Guid>(formatPointer + 24);
                if (subtype != Native.PcmId && subtype != Native.FloatId) throw new NotSupportedException("Unknown extensible sample subtype");
                floating = subtype == Native.FloatId;
            }
            else if (format.Tag is not 1 and not 3) throw new NotSupportedException("Unsupported format tag");
            Meter.Validate(format, floating);
            uint flags = 0x00040000u | (target.HasValue ? 0x80020000u : 0u); // EVENTCALLBACK, optional AUTOCONVERTPCM | LOOPBACK
            Native.Check(client.Initialize(0, flags, target.HasValue ? 0 : 1_000_000, 0, formatPointer, 0));
            Native.Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Native.Check(client.GetService(Native.CaptureClientId, out captureObject));
            var capture = (IAudioCaptureClient)captureObject;
            token.ThrowIfCancellationRequested();
            Native.Check(client.Start()); started = true;
            Program.Emit(new { kind = "started", source, format.Rate, format.Channels, format.Bits, floating, meaning = "API_started_not_source_verified" });
            long nextReport = 0;
            var window = new Meter();
            while (!token.IsCancellationRequested)
            {
                if (targetProcess?.HasExited == true) throw new InvalidOperationException("source_exited; rediscovery and new selection required");
                int wait = WaitHandle.WaitAny([ready, token.WaitHandle], 100);
                if (wait == 1) break;
                Native.Check(capture.GetNextPacketSize(out uint packetFrames));
                while (packetFrames > 0 && !token.IsCancellationRequested)
                {
                    Native.Check(capture.GetBuffer(out nint data, out uint frames, out uint packetFlags, out ulong position, out ulong qpc));
                    if (frames == 0) break;
                    try
                    {
                        window.Add(data, frames, format, floating, packetFlags, qpc, position);
                        totals.Add(data, frames, format, floating, packetFlags, qpc, position);
                        lastPacketMs = clock.ElapsedMilliseconds;
                    }
                    finally { Native.Check(capture.ReleaseBuffer(frames)); }
                    Native.Check(capture.GetNextPacketSize(out packetFrames));
                }
                if (clock.ElapsedMilliseconds >= nextReport)
                {
                    long? age = lastPacketMs < 0 ? null : clock.ElapsedMilliseconds - lastPacketMs;
                    Program.Emit(new { kind = "meter", source, elapsedMs = clock.ElapsedMilliseconds, health = Health.Classify(window.Packets, window.Peak, age), packetAgeMs = age, stats = window.Snapshot() });
                    window = new Meter(); nextReport = clock.ElapsedMilliseconds + 500;
                }
            }
        }
        catch (OperationCanceledException) { Program.Emit(new { kind = "cancelled", source }); }
        catch (Exception error) { result = 2; Program.Emit(new { kind = "capture_error", source, detail = Program.Error(error), action = "stop_this_source_no_automatic_fallback" }); }
        finally
        {
            if (started && client is not null)
            {
                int stop = client.Stop();
                if (stop < 0) { result = 2; Program.Emit(new { kind = "stop_error", source, hresult = $"0x{stop:X8}" }); }
            }
            Native.Release(captureObject); Native.Release(clientObject);
            Native.Release(device); Native.Release(devices); Native.Release(enumerator);
            if (formatPointer != 0) Marshal.FreeCoTaskMem(formatPointer);
            targetProcess?.Dispose();
            if (com) Native.CoUninitialize();
            Program.Emit(new { kind = "stopped", source, elapsedMs = clock.ElapsedMilliseconds, stats = totals.Snapshot(), result, note = "handles_released_by_code_not_external_leak_proof" });
        }
        return result;
    }
}

internal static class Health
{
    // -80 dBFS is a diagnostic floor, not a voice detector. Native silence can contain dither.
    internal static string Classify(long packets, double peak, long? ageMs = null) => ageMs >= 500 ? "stalled_packets_unverified" : packets == 0 ? "no_packets_unverified" : peak == 0 ? "silent_source_unverified" : peak <= 0.0001 ? "low_level_source_unverified" : "signal_present_identity_unverified";
}

internal sealed class Meter
{
    internal long Packets, Frames, Samples, Discontinuities, TimestampErrors, Clipped, NonFinite;
    internal double Peak, Squares;
    internal ulong? LastQpc100ns, LastDevicePosition;
    internal static void Validate(WaveFormat format, bool floating)
    {
        if (format.Rate == 0 || format.Channels == 0 || format.BlockAlign != format.Channels * format.Bits / 8 ||
            (floating ? format.Bits != 32 : format.Bits is not 16 and not 24 and not 32))
            throw new NotSupportedException("Meter supports packed PCM16/24/32 and float32 only");
    }
    internal unsafe void Add(nint data, uint frames, WaveFormat format, bool floating, uint flags, ulong qpc, ulong position)
    {
        Validate(format, floating);
        bool silent = (flags & 2) != 0;
        if (!silent && data == 0) throw new InvalidOperationException("Non-silent packet has null data");
        Packets++; Frames += frames;
        Discontinuities += (flags & 1) != 0 ? 1 : 0;
        TimestampErrors += (flags & 4) != 0 ? 1 : 0;
        LastQpc100ns = (flags & 4) == 0 ? qpc : null;
        LastDevicePosition = (flags & 4) == 0 ? position : null;
        int count = checked((int)(frames * format.Channels));
        Samples += count;
        if (silent) return; // WASAPI may supply a null pointer for silent buffers.
        byte* pointer = (byte*)data;
        for (int i = 0; i < count; i++, pointer += format.Bits / 8)
        {
            double sample = floating ? *(float*)pointer : format.Bits switch
            {
                16 => *(short*)pointer / 32768.0,
                24 => ((pointer[0] << 8 | pointer[1] << 16 | pointer[2] << 24) >> 8) / 8388608.0,
                32 => *(int*)pointer / 2147483648.0,
                _ => throw new NotSupportedException()
            };
            if (!double.IsFinite(sample)) { NonFinite++; continue; }
            double absolute = Math.Abs(sample);
            if (absolute >= 0.999) Clipped++;
            Peak = Math.Max(Peak, absolute); Squares += sample * sample;
        }
    }
    internal object Snapshot() => new { packets = Packets, frames = Frames, peak = Peak, rms = Samples == 0 ? 0 : Math.Sqrt(Squares / Samples), discontinuities = Discontinuities, timestampErrors = TimestampErrors, clippedSamples = Clipped, nonFiniteSamples = NonFinite, lastQpc100ns = LastQpc100ns, lastDevicePosition = LastDevicePosition };
}
