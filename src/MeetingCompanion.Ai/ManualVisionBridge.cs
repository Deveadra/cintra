using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

namespace MeetingCompanion.Ai;

/// <summary>One manual request at a time. Takes ownership of PNG bytes even on rejection and zeroes them.
/// Stop is terminal; a new session requires a new bridge. No capture timer, queue, retry, logging or execution.</summary>
public sealed class ManualVisionBridge : IDisposable
{
    private readonly object gate = new();
    private readonly RollingConversationStore store;
    private readonly IVisionReasoner reasoner;
    private readonly VisionPolicy policy;
    private readonly TimeProvider time;
    private readonly HashSet<Guid> attempted = [];
    private ActiveRequest? active;
    private bool stopped;
    private decimal reserved;

    private sealed class ActiveRequest(byte[] image, VisionRequest request, Guid[] evidence) : IDisposable
    {
        public byte[] Image { get; } = image;
        public VisionRequest? Request { get; private set; } = request;
        public Guid[] Evidence { get; private set; } = evidence;
        public CancellationTokenSource Cancellation { get; } = new();
        public void Clear() { CryptographicOperations.ZeroMemory(Image); Request = null; Evidence = []; }
        public void Dispose() { Clear(); Cancellation.Dispose(); }
    }

    public ManualVisionBridge(RollingConversationStore store, IVisionReasoner reasoner, VisionPolicy? policy = null, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.reasoner = reasoner ?? throw new ArgumentNullException(nameof(reasoner));
        this.policy = policy ?? new();
        this.policy.Validate();
        time = timeProvider ?? TimeProvider.System;
    }

    public decimal ReservedEstimateUsd { get { lock (gate) return reserved; } }

    public async Task<ReasoningResult> AnalyzeManualAsync(SnapshotMetadata metadata, byte[] png, VisionConsent consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        ActiveRequest? operation = null;
        CancellationTokenSource? deadline = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (stopped) return VisionResults.Failure("stopped");
                if (active is not null) return VisionResults.Failure("busy");
                if (consent is not { ApiEnabled: true, ImageEnabled: true }) return VisionResults.Failure("vision_consent_required");
                if (metadata is null || !ValidPng(metadata, png)) return VisionResults.Failure("invalid_image", ReasoningOutcome.Incomplete);
                if (attempted.Contains(metadata.SnapshotId)) return VisionResults.Failure("snapshot_already_attempted");
                if (attempted.Count >= policy.MaxRequests) return VisionResults.Failure("request_limit");
                // GetSnapshot returns context and gap view under one store lock; never fetch latest context later.
                var snapshot = store.GetSnapshot(metadata.CapturedAtMs);
                if (snapshot.View.Stopped) return VisionResults.Failure("conversation_stopped");
                var imageEvent = snapshot.View.Events.OfType<SnapshotAdded>().FirstOrDefault(e => e.Snapshot.SnapshotId == metadata.SnapshotId);
                if (imageEvent is not null && imageEvent.Snapshot != metadata) return VisionResults.Failure("snapshot_metadata_conflict", ReasoningOutcome.Incomplete);
                var imageEventId = imageEvent?.EventId ?? Guid.NewGuid();
                var context = VisionContextBuilder.Build(snapshot, metadata, imageEventId, policy.MaxContextCharacters, store.Clock.ToUtc(metadata.CapturedAtMs));
                var request = new VisionRequest(new(Guid.NewGuid(), TriggerKind.SnapshotRequested, false, context.Context), metadata, png, "image/png");
                var estimate = OpenAiVisionReasoner.EstimateUsd(request);
                if (estimate > consent.ApprovedEstimateUsd || reserved + estimate > policy.SessionBudgetUsd) return VisionResults.Failure("estimated_budget_exceeded");
                // Metadata only, never image bytes. Existing MC-008 event IDs are preserved; otherwise create
                // the manual image's evidence event so the caller can resolve Suggestion.EvidenceEventIds.
                if (imageEvent is null) store.Apply(new SnapshotAdded
                {
                    SchemaVersion = ContractVersion.Current,
                    EventId = imageEventId,
                    SessionId = snapshot.Context.SessionId,
                    ReceivedUtc = time.GetUtcNow(),
                    Snapshot = metadata
                });
                reserved += estimate; // No refunds on uncertain/billed failures; hard bound on admitted requests.
                attempted.Add(metadata.SnapshotId);
                operation = new(png, request, context.EvidenceIds);
                active = operation;
            }

            using var caller = cancellationToken.Register(() => Cancel(operation));
            deadline = new CancellationTokenSource(policy.Timeout, time);
            using var timeout = deadline.Token.Register(() => Cancel(operation));
            Task<ReasoningResult> task;
            lock (gate)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                task = reasoner.AnalyzeAsync(operation.Request!, operation.Cancellation.Token);
            }
            // A broken adapter cannot keep the bridge pending forever or publish a late completion.
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await task.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (stopped || operation.Cancellation.IsCancellationRequested) return VisionResults.Failure(stopped ? "stopped" : deadline.IsCancellationRequested ? "vision_timeout_no_retry" : "cancelled", deadline.IsCancellationRequested ? ReasoningOutcome.TimedOut : ReasoningOutcome.Unavailable);
                return VisionOutput.Validate(result, operation.Request!, operation.Evidence);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) return VisionResults.Failure("cancelled");
            lock (gate) return stopped ? VisionResults.Failure("stopped") : deadline?.IsCancellationRequested == true ?
                VisionResults.Failure("vision_timeout_no_retry", ReasoningOutcome.TimedOut) : VisionResults.Failure("vision_adapter_cancelled_no_retry");
        }
        catch (Exception e) when (e is ContractException or ArgumentException or JsonException)
        { return VisionResults.Failure("invalid_request_or_result", ReasoningOutcome.Incomplete); }
        catch (Exception) { return VisionResults.Failure("vision_adapter_failed_no_retry"); }
        finally
        {
            deadline?.Dispose();
            lock (gate)
            {
                if (operation is not null) { if (ReferenceEquals(active, operation)) active = null; operation.Dispose(); }
                else CryptographicOperations.ZeroMemory(png);
            }
        }
    }

    private void Cancel(ActiveRequest operation)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, operation)) return;
            operation.Clear();
            try { operation.Cancellation.Cancel(); }
            catch (AggregateException) { /* A defective adapter callback must not break Stop or audio orchestration. */ }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            if (active is not null) Cancel(active);
            attempted.Clear();
        }
    }
    public void Dispose() => Stop();

    private bool ValidPng(SnapshotMetadata metadata, byte[] png)
    {
        metadata.Validate();
        if (png.Length < 33 || png.Length > policy.MaxImageBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != metadata.Width ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != metadata.Height ||
            (long)metadata.Width * metadata.Height > 4_194_304) return false;
        return metadata.Crop is null || (metadata.Crop.Width == metadata.Width && metadata.Crop.Height == metadata.Height);
    }
}
