# MC-007 rolling conversation store and handoff

## Scope and baseline

Implemented from accepted main `432d81c21b7136ecc92d2b942e64ff9e2f83026b` in isolated branch
`orion/mc-007-rolling-conversation`. Guardian Protocol is documented in Cintra issues #3/#4:
feature isolation, actual verification, frozen contracts and ownership, no secrets/archives,
no main mutation or self-merge. Changes are limited to Core, Unit tests and this document.

## Consumer contract

`RollingConversationStore` implements the frozen `IConversationStore`. Construct one store per
session with the same **ClockMapping instance/value** passed to audio `StartAsync` and
`DualStreamTranscriber.RunAsync`. `SessionClock` converts QPC ticks using decimal arithmetic
before division (avoiding multiplication overflow), floors to session-relative milliseconds,
and converts capture time to UTC. Transcript, gap, source and snapshot ordering uses capture
time, never provider receipt time. Session state uses health observation time; an empty-health
state has no capture timestamp in v1, so it uses UTC mapped through the shared origin.

Feed typed events through `Apply`, or await
`store.ConsumeAsync(transcriber.ReadAllAsync(token), token)`. The latter has no extra queue;
backpressure comes from the existing bounded MC-006 channel. Completion, cancellation and
failure clear the store in `finally`. The owner must also call `Stop`/`Clear` immediately when
Stop is requested, before waiting for provider shutdown. Both methods and Stopping/Stopped
events are terminal: late writers cannot repopulate content. Create a fresh store on restart.

Turns are keyed by `(StreamId, TurnId)`. Partials are full revised text, matching MC-006;
receipt timestamp and event ID break revision ties, independently of Apply scheduling.
A final replaces its partial. First accepted final is immutable; identical events, new-ID
final duplicates and stale deltas cannot change it. Provider item IDs are metadata, not
cross-stream identities. Final ordering is capture start, capture end, stream, turn ID,
event ID. Overlapping streams remain distinct turns, with explicit intersecting intervals.

`GetContext(asOfMs)` includes only retained finals whose capture end is at or before the
requested time, explicit evidence-linked issues and latest snapshot ID. `GetSnapshot(asOfMs)`
atomically returns that frozen context plus a Core `ConversationView` containing provisional
turns, raw gaps/source changes, component health, issue IDs, overlaps and interrupted turn IDs.
Interrupted turns intersect a retained same-stream gap, source boundary or loss heartbeat;
the store preserves observed text and does not manufacture continuity or missing speech.
Health expiration becomes Unknown/degraded. Source replacement requires both capture and STT
health newer than the boundary before a previously reported Listening state is trusted.
This is a context view, not a replacement for the existing SessionStateMachine orchestration.

## Retention and evidence

Default limits: 10 capture minutes, 2048 events, 262144 retained event text characters,
128 issue statements, 2048 characters and 16 references per statement, 256 overlap intervals,
2000 ms health freshness. All are configurable positive values. Event metadata strings count
against the event text limit. Issues have a separate count/text/reference bound. Overlap
truncation is explicit in the view; health has at most the six frozen component types.

Watermark is the greatest accepted capture/observation end. Time eviction runs on Apply;
count/text pressure evicts the oldest capture-ordered entry. Queries also apply their own
as-of window without advancing the ingest watermark, so future queries cannot expose old
content. Event IDs and turn identity are retained only within these bounds. There is no
unbounded dedupe history, archive, raw audio, pixels, disk writer or provider call.
Clear releases references; it cannot erase copies already returned to callers or guarantee
secure erasure of immutable managed strings. Owners must drop their context/view copies on Stop.

`RecordIssue` records explicit caller observations for reported errors, confirmed facts,
environment, hypotheses, attempts/failures, suggested steps, topics and open questions.
References must resolve to a retained final event or snapshot metadata already available at
the statement's recorded capture time. Partial, missing, assumption and future tool-result
references are rejected. Classification is caller supplied; no NLP classifier or suggestion
generator is implemented. `ResolveIssue(id)` removes resolved questions/obsolete statements.
IDs make repeated issue submissions idempotent. Statement and health arrays are copied on
entry and return. Evicting evidence also evicts dependent issues, preventing dangling facts.
Summary is a deterministic compact rendering of explicit retained issue statements. It is
not an inferred narrative or an older-call archive; older evidence is deliberately forgotten.

## Verification

Commands run in the isolated Windows checkout, .NET SDK 10.0.401, Windows SDK 10.0.26100.0:

- `./scripts/build.ps1`: initial full Release build passed with 0 warnings/errors.
- `dotnet build MeetingCompanion.slnx -c Release --no-restore -m:1`: final-source build passed, 0 warnings/errors.
- `./scripts/test.ps1`: 111/111 managed tests passed (39 Unit, 27 Contract, 16 STT, 18 Capture, 11 Audio), plus synthetic playback PASS.
- `./scripts/native-build.ps1`: MSVC x64 build and both existing native offline tests passed.
- `dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore`: passed.
- `dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts`
  followed by `git diff --exit-code -- docs/contracts`: passed, frozen schema content unchanged.

A parallel rebuild after the first full build hit MSB4166 (worker exited). Verification was
repeated on the final source with one build worker; results below refer to that rebuilt source.
Focused tests cover 40 randomized deterministic arrival replays, capture overlap and truncation,
partial revision/duplicate finals, loss and recovery health, shared snapshot clock/evidence,
input/output ownership, as-of isolation, time/count/text/issue limits, unknown evidence rejection,
500 concurrent writers/readers, Stop races, completion/cancellation/failure cleanup, and wrong sessions.

Live paid providers, hardware capture, Zoom/Teams calls, acoustic isolation, screenshot UI and
reasoning quality were not tested by this task. No synthetic result claims those capabilities.

## MC-009 requirements

- Pass the meeting ClockMapping to SnapshotCapture; replace the current shell-specific origin
  in that task's integration scope. Register validated SnapshotAdded metadata in this store.
- Use `GetSnapshot(metadata.CapturedAtMs)` for atomic transcript/issue context plus gaps and health.
  Handle empty/evicted context and interrupted turns explicitly. Pixels stay with capture/vision,
  never in this store. Evidence references use event IDs and snapshot IDs, not provider IDs.
- Record visible observations with snapshot evidence only after actual analysis/confirmation;
  a reference's existence does not prove a model's interpretation is true. Preserve uncertainty.
- Stop must cancel outstanding vision work and release image/context copies in the bridge.

## MC-010 requirements

- Consume stable partials/finals from the Core view; use `(session, stream, turn)` identities for
  bounded classifier/deduper state. Finals supersede partial candidates. The store does not trigger AI.
- Consult gaps, interrupted turns, overlap truncation and fresh health before classification.
  Missing remote health or unisolated fallback attribution must not become confident named speech.
- Submit explicit grounded issue statements using retained evidence and resolve obsolete questions;
  do not promote guesses into confirmed facts. Maintain separate bounded cooldown/attempt dedupe.
- Queries return capture-bounded present knowledge, not historical revision snapshots or complete
  call history. Snapshot/trigger relevance scoring and provider quality evaluation remain downstream.

## Known limits / next safest action

Review and run PR CI before merge. First final wins if a producer emits conflicting finals for
one turn: such a producer violates the MC-006 final contract; there is no automatic correction API.
Partials use receipt metadata only for revision selection because v1 lacks a revision counter.
Retention intentionally loses older evidence and dedupe identities. The summary is extractive issue
state only; semantic compression is not implemented. The frozen ConversationContext has no gap or
health fields; consumers must use the paired Core view. No desktop wiring was changed in this task.
