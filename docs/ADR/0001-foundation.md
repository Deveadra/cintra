# ADR 0001: Managed foundation and contract v1

Status: implemented for MC-001, pending review. Date: 2026-10-09.

## Structure and dependencies

Use .NET 10 LTS, pinned to the installed 10.0.401 SDK feature band with patch roll-forward.
Contracts and Core target net10.0 and avoid Windows dependencies so domain tests remain offline.
Desktop targets net10.0-windows/WPF, x64. Managed adapter projects reference Contracts;
Desktop currently references only Core. Native C++ build integration belongs to MC-003–005.
No native project or experiment is created or edited by this task.

Contracts owns wire DTOs, source identity, clocks, bounded framing and adapter interfaces.
Core owns pure lifecycle policy. Audio owns capture orchestration/conversion/pipe host integration;
Stt owns one independent transport per selected stream plus local VAD; Ai owns text and vision
reasoning behind distinct interfaces; Capture owns manual screenshots; Platform owns discovery.
No provider SDKs, API schemas, credentials or claimed model capability are part of this foundation.

## Wire compatibility

Version 1 uses UTF-8 snake_case JSON and string enums; attribution labels are uppercase.
Conversation events preserve the plan's flat transcription shape. Other event variants have typed
payloads. Polymorphism is explicitly allowlisted, never driven by arbitrary CLR type names.
IPC has schema_version, message_id, session_id and a payload discriminated by message_type.
Unknown optional properties are ignored for additive compatibility; missing required fields,
unknown enum/discriminator values, future versions, null required data and invalid semantics fail.
Breaking changes require a new version and compatibility tests; regenerated schemas alone do not
authorize changes. Exported JSON Schemas describe wire structure; runtime Validate additionally
enforces ranges, finite values, limits, source relationships and automatic-command policy.

## Audio and time

Microphone and incoming capture remain separate. Incoming can be remote_app or explicitly acknowledged
endpoint_fallback; the latter is always unisolated and cannot label its transcript REMOTE or YOU.
No source contract identifies named remote speakers. Mic attribution still needs echo assessment.
All capture intervals use session-relative monotonic milliseconds, correlated through a shared clock
origin/tick frequency and UTC origin. Sequence numbers are per stream, reset only for a new session;
reconnect does not authorize silently replaying audio. Sources are selected by endpoint ID or PID tree.

IPC carries normalized PCM16 mono at 24 kHz, little-endian, encoded as JSON base64 in v1. A frame is
at most one second; expected operational batches are 20 ms. Base64 is chosen for an auditable initial
C++/C# boundary; a binary optimization requires measured need and versioned negotiation. No resampling
implementation is included. The runtime must bound capture queues to two seconds, emit exact gap
intervals on loss, and never block the native callback on serialization, IPC or network activity.

## Lifecycle responsibilities

SessionStateMachine is a lock-serialized pure policy object with caller-supplied monotonic virtual time.
Listening requires fresh healthy/silence heartbeats from both capture and both STT components.
Silence means a functioning, verified source with no speech; zero_level/wrong_process are unhealthy.
Reasoning and snapshot failures have separate health and do not disable transcription.
Call Tick at least every 250 ms; TTL defaults to 2 seconds. ReportHealth does not recover implicitly.
Start/recover/resume clear health. Adapters must discard callbacks from previous operation generations;
the owner must not feed callbacks from old sessions into a new machine.

CanSendAudio means lifecycle permission only: it allows healthy streams to continue during degradation;
the orchestrator must also gate each stream on transport/capture readiness. CanGenerate additionally
requires a fresh reasoning heartbeat. Pause/stop/cancel/error remove outbound permission immediately.
The owner cancels outstanding work and acknowledges Stopped only after buffers, sockets and helper
are released. Startup/recovery/stop timeout goes to Error, never a false Stopped or Listening.
Retry means explicit cleanup followed by source selection, not blind reuse of failed resources.
FSM tests prove policy, not actual device or provider cleanup. No default persistence exists.

## Deferred work

Per-user named-pipe ACLs and peer validation, helper process lifecycle, resampling, real audio,
provider reconnect/deduplication, VAD, transcript ordering/storage, trigger policy and screenshot UX
remain with their backlog owners. Evidence references and command risk tags express claims, not proof:
MC-007/011 must resolve references and verify semantics before displaying model output. No command
execution capability is exposed. Full replay quality metrics belong to MC-010/011/016.
