using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsAudioSpike;

// Explicit, bounded synthetic playback fixture. Never routes microphone data.
internal static class Tone
{
    internal static unsafe int Run(string[] args)
    {
        if (args.Length != 4 || args[1] != "--seconds" || args[3] != "--acknowledge-playback" ||
            !int.TryParse(args[2], out int seconds) || seconds is < 1 or > 10)
            throw new ArgumentException("tone --seconds <1..10> --acknowledge-playback (440 Hz, amplitude 0.03)");
        Native.Check(Native.CoInitializeEx(0, 0));
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? clientObject = null, renderObject = null;
        IAudioClient? client = null;
        nint formatPointer = 0;
        bool started = false;
        int result = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 5));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            enumerator = Native.Enumerator();
            Native.Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
            Program.Emit(new { kind = "tone_notice", pid = Environment.ProcessId, seconds, frequencyHz = 440, amplitude = 0.03, endpoint = Inventory.Name(device) });
            Native.Check(device.Activate(Native.AudioClientId, 23, 0, out clientObject));
            client = (IAudioClient)clientObject;
            var format = new WaveFormat { Tag = 1, Channels = 2, Rate = 48000, Bits = 16, BlockAlign = 4, BytesPerSecond = 192000 };
            formatPointer = Marshal.AllocCoTaskMem(18);
            Marshal.StructureToPtr(format, formatPointer, false);
            Native.Check(client.Initialize(0, 0x80000000, 1_000_000, 0, formatPointer, 0));
            Native.Check(client.GetBufferSize(out uint size));
            Native.Check(client.GetService(Native.RenderClientId, out renderObject));
            var render = (IAudioRenderClient)renderObject;
            Native.Check(client.Start()); started = true;
            cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
            long frame = 0;
            while (!cancellation.IsCancellationRequested)
            {
                Native.Check(client.GetCurrentPadding(out uint padding));
                uint available = size - padding;
                if (available > 0)
                {
                    Native.Check(render.GetBuffer(available, out nint buffer));
                    try
                    {
                        var samples = (short*)buffer;
                        for (uint i = 0; i < available; i++, frame++)
                        {
                            short sample = (short)(Math.Sin(2 * Math.PI * 440 * frame / 48000) * 0.03 * short.MaxValue);
                            samples[2 * i] = sample; samples[2 * i + 1] = sample;
                        }
                    }
                    finally { Native.Check(render.ReleaseBuffer(available, 0)); }
                    if (frame == available) Program.Emit(new { kind = "tone_ready", pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow });
                }
                cancellation.Token.WaitHandle.WaitOne(10);
            }
        }
        catch (Exception error) { result = 2; Program.Emit(new { kind = "tone_error", detail = Program.Error(error) }); }
        finally
        {
            Console.CancelKeyPress -= handler;
            if (started && client is not null)
            {
                int hr = client.Stop();
                if (hr < 0) { result = 2; Program.Emit(new { kind = "tone_stop_error", hresult = $"0x{hr:X8}" }); }
            }
            Native.Release(renderObject); Native.Release(clientObject); Native.Release(device); Native.Release(enumerator);
            if (formatPointer != 0) Marshal.FreeCoTaskMem(formatPointer);
            Native.CoUninitialize();
            Program.Emit(new { kind = "tone_stopped", result });
        }
        return result;
    }
}
