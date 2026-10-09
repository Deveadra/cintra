using MeetingCompanion.Contracts;

namespace MeetingCompanion.Audio;

/// <summary>Two seconds per source, bounded metadata, explicit overflow intervals.</summary>
public sealed class CaptureInbox
{
    private readonly object gate = new();
    private readonly LinkedList<CaptureMessage> messages = new();
    private readonly Dictionary<AudioStreamId, int> samples = new();
    private readonly SemaphoreSlim available = new(0);
    private bool complete;

    public void Add(CaptureMessage message)
    {
        message.Validate();
        lock (gate)
        {
            if (complete) return;
            if (message is AudioFrame frame)
            {
                samples.TryGetValue(frame.StreamId, out var count);
                var size = frame.Pcm.Length / 2;
                long? first = null;
                long end = 0;
                long lost = 0;
                var node = messages.First;
                while (count + size > 48_000 && node is not null)
                {
                    var next = node.Next;
                    if (node.Value is AudioFrame old && old.StreamId == frame.StreamId)
                    {
                        first ??= old.CapturedStartMs;
                        end = old.CapturedEndMs;
                        lost += old.Pcm.Length / 2;
                        count -= old.Pcm.Length / 2;
                        messages.Remove(node);
                    }
                    node = next;
                }
                if (first is not null) messages.AddLast(new CaptureGap(frame.StreamId, first.Value, end, GapReason.QueueOverflow, lost));
                samples[frame.StreamId] = count + size;
            }
            if (messages.Count >= 512) throw new IOException("Capture metadata queue overflow; transport must stop.");
            messages.AddLast(message);
            // Wakeup signal, not a count of messages: removal/clear cannot strand readers.
            if (available.CurrentCount == 0) available.Release();
        }
    }

    public void ClearAudio()
    {
        lock (gate)
        {
            for (var node = messages.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value is AudioFrame) messages.Remove(node);
                node = next;
            }
            samples.Clear();
        }
    }

    public void Clear()
    {
        lock (gate) { messages.Clear(); samples.Clear(); }
    }

    public void Complete(bool clear = false)
    {
        lock (gate)
        {
            complete = true;
            if (clear) { messages.Clear(); samples.Clear(); }
            if (available.CurrentCount == 0) available.Release();
        }
    }

    public async IAsyncEnumerable<CaptureMessage> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            CaptureMessage? message;
            lock (gate)
            {
                message = messages.First?.Value;
                if (message is not null)
                {
                    messages.RemoveFirst();
                    if (message is AudioFrame frame) samples[frame.StreamId] -= frame.Pcm.Length / 2;
                }
                else if (complete) yield break;
            }
            if (message is not null) yield return message;
            else await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
