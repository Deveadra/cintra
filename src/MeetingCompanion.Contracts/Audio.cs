using System.Text.Json.Serialization;

namespace MeetingCompanion.Contracts;

public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public static AudioFormat Normalized { get; } = new(24_000, 1, 16);
}

/// <summary>Endpoint fallback is an explicitly selected, unisolated source.</summary>
public sealed record CaptureSource(AudioStreamId StreamId, string DisplayName, string? DeviceId,
    int? ProcessId, bool IncludeProcessTree, bool UnisolatedAcknowledged)
{
    public void Validate()
    {
        Require.Enum(StreamId);
        Require.Text(DisplayName, 256);
        if (StreamId == AudioStreamId.RemoteApp)
            Require.That(ProcessId > 0 && IncludeProcessTree && DeviceId is null, "Remote capture requires a selected process tree.");
        else
        {
            Require.Text(DeviceId, 1024);
            Require.That(ProcessId is null && !IncludeProcessTree, "Endpoint capture cannot select a process.");
        }
        Require.That(StreamId != AudioStreamId.EndpointFallback || UnisolatedAcknowledged,
            "System output fallback requires explicit acknowledgement.");
    }
}

public sealed record CaptureSelection(CaptureSource Microphone, CaptureSource Incoming)
{
    public void Validate()
    {
        Require.That(Microphone is not null && Incoming is not null, "Both sources are required.");
        Microphone!.Validate();
        Incoming!.Validate();
        Require.That(Microphone.StreamId == AudioStreamId.LocalMic && Incoming.StreamId != AudioStreamId.LocalMic,
            "Microphone and incoming streams must be separate.");
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "message_type")]
[JsonDerivedType(typeof(StartCapture), "start_capture")]
[JsonDerivedType(typeof(CaptureControl), "capture_control")]
[JsonDerivedType(typeof(AudioFrame), "audio_frame")]
[JsonDerivedType(typeof(CaptureHealth), "capture_health")]
[JsonDerivedType(typeof(CaptureGap), "capture_gap")]
[JsonDerivedType(typeof(CaptureSourceChanged), "capture_source_changed")]
public abstract record CaptureMessage
{
    public abstract void Validate();
}

public sealed record StartCapture(CaptureSelection Selection, ClockMapping Clock) : CaptureMessage
{
    public override void Validate()
    {
        Require.That(Selection is not null && Clock is not null, "Selection and clock required.");
        Selection!.Validate();
        Clock!.Validate();
    }
}

public enum CaptureAction { Pause, Resume, Stop }
public sealed record CaptureControl(CaptureAction Action) : CaptureMessage
{
    public override void Validate() => Require.Enum(Action);
}

/// <summary>Owned byte array, signed little-endian PCM16. Receiver must not retain beyond its bounded queue.</summary>
public sealed record AudioFrame(AudioStreamId StreamId, long Sequence, long CapturedStartMs,
    long CapturedEndMs, AudioFormat Format, int SourceSampleRate, byte[] Pcm) : CaptureMessage
{
    public override void Validate()
    {
        Require.Enum(StreamId);
        Require.Range(CapturedStartMs, CapturedEndMs);
        Require.That(Sequence >= 0 && SourceSampleRate > 0, "Invalid frame metadata.");
        Require.That(Format == AudioFormat.Normalized, "v1 accepts only 24 kHz mono PCM16.");
        Require.That(Pcm is { Length: > 0 and <= ContractVersion.MaxAudioBytes } && Pcm.Length % 2 == 0,
            "Invalid PCM payload size.");
        var duration = CapturedEndMs - CapturedStartMs;
        Require.That(duration <= 1000 && Math.Abs(Pcm!.Length / 48d - duration) < 1,
            "PCM duration and capture interval disagree.");
    }
}

public sealed record CaptureHealth(AudioStreamId StreamId, ComponentHealth Health, double Peak,
    long DroppedFrames, long OldestFrameAgeMs) : CaptureMessage
{
    public override void Validate()
    {
        Require.Enum(StreamId);
        Require.That(Health is not null, "Health required.");
        Health!.Validate();
        Require.That(Health.Component == (StreamId == AudioStreamId.LocalMic ? Component.LocalCapture : Component.RemoteCapture),
            "Health component does not match source.");
        Require.That(double.IsFinite(Peak) && Peak is >= 0 and <= 1 && DroppedFrames >= 0 && OldestFrameAgeMs >= 0,
            "Invalid capture metrics.");
    }
}

public sealed record CaptureGap(AudioStreamId StreamId, long CapturedStartMs, long CapturedEndMs,
    GapReason Reason, long DroppedFrames) : CaptureMessage
{
    public override void Validate()
    {
        Require.Enum(StreamId);
        Require.Enum(Reason);
        Require.Range(CapturedStartMs, CapturedEndMs);
        Require.That(DroppedFrames >= 0, "Dropped frame count cannot be negative.");
    }
}

public sealed record CaptureSourceChanged(CaptureSource Source, long EffectiveAtMs) : CaptureMessage
{
    public override void Validate()
    {
        Require.That(Source is not null && EffectiveAtMs >= 0, "Invalid source change.");
        Source!.Validate();
    }
}

public sealed record IpcEnvelope(int SchemaVersion, Guid MessageId, Guid SessionId, CaptureMessage Payload)
{
    public void Validate()
    {
        Require.That(SchemaVersion == ContractVersion.Current, "Unsupported IPC schema version.");
        Require.Id(MessageId);
        Require.Id(SessionId);
        Require.That(Payload is not null, "IPC payload required.");
        Payload!.Validate();
    }
}
