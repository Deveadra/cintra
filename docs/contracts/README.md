# Version 1 integration contract

Canonical DTOs/interfaces: `src/MeetingCompanion.Contracts`. Frozen structural exports:
`ipc-v1.schema.json`, `events-v1.schema.json`, `suggestion-v1.schema.json`.
These are application contracts, not vendor API payloads. Follow the ADR for semantic invariants.

## Native/audio implementer handoff

1. Implement IAudioCaptureSession on the managed host; use one session UUID for the helper and both streams.
2. Create a local-only Windows named pipe with an explicit current-user ACL and authenticated peer;
   do not create a TCP listener. Pipe creation/security belongs to MC-005, not IpcFraming.
3. Send a StartCapture in an IpcEnvelope (schema_version=1), including selected mic and incoming source
   plus the shared monotonic clock mapping. A future version is rejected; never silently downgrade.
4. Frame each envelope with a **4-byte little-endian signed positive byte length**, then exactly that
   many UTF-8 bytes. Maximum JSON envelope: 1,048,576 bytes. PCM bytes become base64 JSON strings.
   Audio bytes are capped at 48,000 per frame; mono PCM16 is little-endian 24,000 Hz.
5. Return AudioFrame, CaptureHealth, CaptureGap and CaptureSourceChanged payloads. Sequence is per stream.
   Intervals are session-relative milliseconds; source_sample_rate records the pre-conversion rate.
   Report dropped frames and uncertain intervals explicitly. Max queued audio is two seconds per stream.
6. Handle Pause/Resume/Stop controls; a capture-health Stopped acknowledgement for both streams and
   successful helper termination are prerequisites to overall Stopped. No new frames after pause/stop.
7. Use exactly one reader and one serialized writer per pipe. Calls take finite deadlines and cancellation.
   A null read means clean EOF; EOF mid-frame, malformed JSON, version mismatch, timeout or cancellation
   invalidates that connection. Dispose it; do not resume parsing a partly consumed envelope. Reconnect
   with bounded backoff, fresh source resolution and gap reporting; do not replay uncertain audio.

Health heartbeats must distinguish verified silence, zero level, wrong process, no device, denied access
and disconnection. Microphone defaults cannot silently replace a selected device. Changing from process
capture to unisolated endpoint capture requires new user acknowledgement and a SourceChanged event.

## Other boundaries

- ITranscriptionSession has one StreamId per instance, accepts audio and client-VAD turn commits, and
  emits partial/final/gap/source events. It accepts neither images nor reasoning. Commit idempotency,
  provider item correlation, health reporting to the orchestrator and stale-delta suppression belong to MC-006.
- IConversationStore consumes typed events and provides bounded, evidence-linked context; ordering,
  evidence resolution and retention implementation belong to MC-007. Clear is required on Stop.
- ISuggestionGenerator is text-only; IVisionReasoner accepts explicit still-image requests and the same
  context. Validate(automatic: true) disallows modifying/unknown-risk commands in automatic suggestions.
  Unknown identifiers use explicit templates and required_inputs. Commands remain display/copy only.
- ICallSourceAdapter preserves the plan's Discover/Probe/Resolve boundary, independent of calendars.

No runtime provider or native compatibility is established by schema/contract tests.
