using System.Runtime.InteropServices;

namespace WindowsAudioSpike;

internal static class Tests
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++; Program.Emit(new { kind = "test_pass", name });
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
    internal static unsafe int Run()
    {
        passed = 0;
        Check(Marshal.SizeOf<PropVariant>() == 24 && Marshal.SizeOf<WaveFormat>() == 18 && Marshal.SizeOf<ActivationParameters>() == 12, "x64 ABI sizes");
        Throws<ArgumentException>(() => Options.Parse(["capture", "--pid", "self", "--seconds", "1"]), "capture acknowledgement required");
        Throws<ArgumentException>(() => Options.Parse(["capture", "--seconds", "1", "--acknowledge-capture"]), "explicit source required");
        Throws<ArgumentException>(() => Options.Parse(["capture", "--pid", "0", "--seconds", "1", "--acknowledge-capture"]), "zero PID rejected");
        Throws<ArgumentException>(() => Options.Parse(["capture", "--pid", "1", "--seconds", "31", "--acknowledge-capture"]), "duration bounded");
        Throws<ArgumentException>(() => Options.Parse(["capture", "--pid", "1", "--pid", "2"]), "duplicate source rejected");
        Check(Options.Parse(["capture", "--pid", "self", "--mic", "0", "--seconds", "1", "--acknowledge-capture"]).Mic == 0, "dual source parse");
        var format = new WaveFormat { Tag = 1, Rate = 24000, Channels = 1, Bits = 16, BlockAlign = 2 };
        short[] pcm = [0, 16384, -16384, -32768];
        var mic = new Meter();
        fixed (short* data = pcm) mic.Add((nint)data, 4, format, false, 0, 123, 42);
        Check(mic.Peak == 1 && Math.Abs(mic.Squares - 1.5) < 1e-10 && mic.Frames == 4 && mic.Clipped == 1, "PCM16 level calculation");
        var remote = new Meter();
        remote.Add(0, 4, format, false, 2, 124, 43);
        Check(remote.Peak == 0 && mic.Peak == 1 && remote.Frames == 4, "independent meters and null silent buffer");
        remote.Add(0, 1, format, false, 7, 125, 44);
        Check(remote.Discontinuities == 1 && remote.TimestampErrors == 1 && remote.LastQpc100ns == null, "gap and invalid timestamp flags");
        Throws<InvalidOperationException>(() => new Meter().Add(0, 1, format, false, 0, 0, 0), "invalid non-silent null pointer");
        var floatFormat = format; floatFormat.Bits = 32; floatFormat.BlockAlign = 4;
        float[] floats = [0.25f, -0.5f, float.NaN];
        var floatMeter = new Meter();
        fixed (float* data = floats) floatMeter.Add((nint)data, 3, floatFormat, true, 0, 1, 1);
        Check(floatMeter.Peak == 0.5 && floatMeter.NonFinite == 1, "float32 and non-finite protection");
        var packed = format; packed.Bits = 24; packed.BlockAlign = 3;
        byte[] pcm24 = [0, 0, 128, 0, 0, 64];
        var meter24 = new Meter();
        fixed (byte* data = pcm24) meter24.Add((nint)data, 2, packed, false, 0, 0, 0);
        Check(meter24.Peak == 1 && meter24.Squares == 1.25, "signed packed PCM24");
        int[] pcm32 = [int.MinValue, 1073741824];
        var meter32 = new Meter();
        fixed (int* data = pcm32) meter32.Add((nint)data, 2, floatFormat, false, 0, 0, 0);
        Check(meter32.Peak == 1 && meter32.Squares == 1.25, "PCM32");
        var invalid = format; invalid.Bits = 8;
        Throws<NotSupportedException>(() => Meter.Validate(invalid, false), "unsupported format rejected");
        Check(Health.Classify(0, 0) == "no_packets_unverified", "missing packets not healthy");
        Check(Health.Classify(1, 0) == "silent_source_unverified", "silence not source proof");
        Check(Health.Classify(1, 1.0 / 32768) == "low_level_source_unverified", "one-LSB dither not meaningful signal");
        Check(Health.Classify(1, 0.2) == "signal_present_identity_unverified", "signal not identity proof");
        Check(Health.Classify(1, 0.2, 600) == "stalled_packets_unverified", "old signal does not hide packet stall");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var abandoned = new Activation(1);
        Throws<OperationCanceledException>(() => abandoned.Wait(cancelled.Token), "activation cancellation bounded");
        abandoned.ActivateCompleted(new FailedOperation());
        Check(abandoned.Blob.BlobData == 0, "late completion frees activation parameters");
        var failed = new Activation(1); failed.ActivateCompleted(new FailedOperation());
        Throws<UnauthorizedAccessException>(() => failed.Wait(CancellationToken.None), "activation HRESULT propagated");
        var timedOut = new Activation(1);
        Throws<TimeoutException>(() => timedOut.Wait(CancellationToken.None), "activation timeout bounded");
        timedOut.ActivateCompleted(new FailedOperation());
        Check(timedOut.Blob.BlobData == 0, "completion after timeout safe");
        for (int i = 0; i < 100; i++)
        {
            var cycle = new Activation(1); cycle.ActivateCompleted(new FailedOperation());
            try { cycle.Wait(CancellationToken.None); } catch (UnauthorizedAccessException) { }
            if (cycle.Blob.BlobData != 0) throw new InvalidOperationException("parameter leak");
        }
        Check(true, "100 fake activation failure cycles; not hardware handle proof");
        Program.Emit(new { kind = "test_summary", passed, hardware = false });
        return 0;
    }
    private sealed class FailedOperation : IActivateOperation
    {
        public int GetActivateResult(out int activationResult, out object result)
        {
            activationResult = unchecked((int)0x80070005); result = null!; return 0;
        }
    }
}
