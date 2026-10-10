using MeetingCompanion.Contracts;

namespace MeetingCompanion.Stt;

public sealed record VadOptions(
    double RmsThreshold = 0.012,
    int StartVoicedMs = 40,
    int EndSilenceMs = 400,
    int PreRollMs = 120,
    int MaxTurnMs = 15_000)
{
    public void Validate()
    {
        if (!double.IsFinite(RmsThreshold) || RmsThreshold is <= 0 or >= 1 ||
            StartVoicedMs is < 20 or > 1000 || EndSilenceMs is < 20 or > 5000 ||
            PreRollMs is < 0 or > 1000 || MaxTurnMs is < 1000 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(VadOptions), "Invalid VAD limits.");
    }
}

public sealed record VadTurn(Guid TurnId, long StartMs, long EndMs);
public sealed record VadResult(IReadOnlyList<AudioFrame> Frames, VadTurn? EndedTurn);

/// <summary>Per-source energy VAD. Frames remain transient and are never written to disk.</summary>
public sealed class LocalVad
{
    private readonly VadOptions options;
    private readonly Queue<AudioFrame> preRoll = new();
    private int preRollMs;
    private int candidateMs;
    private int silenceMs;
    private Guid turnId;
    private long turnStartMs;
    private long lastEndMs;

    public LocalVad(VadOptions? options = null)
    {
        this.options = options ?? new VadOptions();
        this.options.Validate();
    }

    public bool InTurn => turnId != Guid.Empty;
    public long TurnStartMs => turnStartMs;
    public long LastEndMs => lastEndMs;

    public VadResult Process(AudioFrame frame)
    {
        frame.Validate();
        var lengthMs = checked((int)(frame.CapturedEndMs - frame.CapturedStartMs));
        var voiced = Rms(frame.Pcm) >= options.RmsThreshold;
        lastEndMs = frame.CapturedEndMs;
        if (!InTurn)
        {
            preRoll.Enqueue(frame);
            preRollMs += lengthMs;
            candidateMs = voiced ? candidateMs + lengthMs : 0;
            while (preRollMs > Math.Max(options.PreRollMs + options.StartVoicedMs, lengthMs) && preRoll.Count > 1)
            {
                var removed = preRoll.Dequeue();
                preRollMs -= checked((int)(removed.CapturedEndMs - removed.CapturedStartMs));
            }
            if (candidateMs < options.StartVoicedMs) return new VadResult([], null);
            turnId = Guid.NewGuid();
            turnStartMs = preRoll.Peek().CapturedStartMs;
            silenceMs = 0;
            var opening = preRoll.ToArray();
            preRoll.Clear();
            preRollMs = 0;
            candidateMs = 0;
            return new VadResult(opening, null);
        }

        silenceMs = voiced ? 0 : silenceMs + lengthMs;
        var end = silenceMs >= options.EndSilenceMs || frame.CapturedEndMs - turnStartMs >= options.MaxTurnMs;
        if (!end) return new VadResult([frame], null);
        var turn = new VadTurn(turnId, turnStartMs, frame.CapturedEndMs);
        Reset();
        return new VadResult([frame], turn);
    }

    public VadTurn? Abandon()
    {
        var turn = InTurn ? new VadTurn(turnId, turnStartMs, lastEndMs) : null;
        Reset();
        return turn;
    }

    private void Reset()
    {
        turnId = Guid.Empty;
        turnStartMs = 0;
        silenceMs = 0;
        candidateMs = 0;
        preRoll.Clear();
        preRollMs = 0;
    }

    private static double Rms(byte[] pcm)
    {
        double sum = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | pcm[i + 1] << 8);
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }
        return Math.Sqrt(sum / (pcm.Length / 2));
    }
}
