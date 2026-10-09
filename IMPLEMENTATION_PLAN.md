# Real-Time Meeting Companion — Agent Implementation Plan

**Specification version:** 1.0  
**Prepared:** 2026-10-09  
**Status:** Proposed implementation; **not** an assertion that any code already exists  
**Primary target:** Windows 11 desktop; Zoom and Microsoft Teams desktop meetings and 1:1 calls  
**Secondary target:** Other Windows calling applications and browser-based conferencing via a documented fallback  
**Primary deliverable:** Dependable, low-interruption live meeting assistance with separated microphone and incoming-call transcription, proactive suggestions, and manual one-click visual snapshots.

> **Instruction to every implementation agent:** Treat this document as the product contract, not as evidence of repository state. Inspect the actual repository and running environment before editing. Preserve user files, Git history, and working features. Implement verifiable slices, write tests, and report exact evidence; never claim that Zoom/Teams or any API is working without end-to-end proof. Do not silently expand MVP scope.

---

## 1. Product charter

### 1.1 Problem

During Zoom, Teams, and ordinary desktop calls, the operator needs fast, private written support without pausing the discussion to take screenshots, dictate context, or type prompts. The companion should hear the operator separately from remote participants, track the discussion, spot questions and technical issues, and offer useful answers or next diagnostic steps. If an error, code, terminal, or diagram is relevant, the operator should press **Snapshot** to include the visible evidence instantly.

### 1.2 Required outcomes

1. Capture the **operator's microphone** as `local_mic` and a **selected application's rendered incoming audio** as `remote_app` in separate, synchronized streams.
2. Transcribe each stream live; show clearly labeled, partial and final turns; do not conflate `YOU` with `REMOTE` merely because both voices emerge from one speaker system.
3. Detect likely questions directed to the operator, technical blockers, missing information, and actionable troubleshooting opportunities. Distinguish direct answers from general discussion.
4. Display **short, useful written suggestions** in an always-on-top panel without speaking in the call or running commands.
5. Provide **one-click Snapshot** and a configurable global shortcut. Offer active-window and selected-region options. Send a single image plus relevant transcript context to a vision-capable reasoning endpoint.
6. Work well in **Zoom meetings, Zoom direct calls, Teams meetings, and Teams direct calls**; select or repair the audio source without forcing meeting-bot integration.
7. Provide manual app/source selection for other call clients and a carefully labeled endpoint-loopback fallback when process-level audio selection is unavailable.
8. Operate in memory by default: live audio buffers are short-lived; no default long audio recordings or recurring screen captures.
9. Survive interruptions (network loss, device changes, app restart, call handoff) with explicit degraded state and recovery; never silently claim listening when capture or transcription is broken.
10. Report latency, capture health, transcription health, and measured quality; test against real applications rather than mocks alone.

### 1.3 Out of scope for v1

- Continuous screen/video observation; automated screenshots; general desktop autonomy.
- Running shell commands, editing source code, sending messages, sharing the screen, or speaking into calls on the user's behalf.
- Guaranteed named identification of every remote participant (mixed call output only gives `REMOTE` until a separate diarization/integration feature is proven).
- Zoom SDK/Teams bot/Graph meeting ingestion in the primary path; browser-tab-level isolation unless proven feasible.
- Persistent meeting recording, cloud transcript history, retrospective summaries, cross-meeting memory, integrations with user accounts.
- Building on top of Katcha or modifying Aerith as a dependency. Integrations can be added later behind interfaces.

### 1.4 User experience (core workflows)

**A. Incoming question.** The operator joins a Teams call, selects Teams as the incoming source, presses Start. Remote participant asks a question. The app recognizes it is probably directed at the operator and surfaces **“Suggested reply”** within the target latency window, with a shorter option and confidence/uncertainty where warranted. Suggestions appear only when useful; never require the operator to type.

**B. Live debugging.** The group discusses a failing Kubernetes workload. The app remembers which checks have already been mentioned, distinguishes observations from theories, and suggests a **read-only** command and what to inspect in the output. No fabricated resources, paths, namespaces, or logs. Any command requiring unknown identifiers must clearly name the information required before execution.

**C. Snapshot-assisted diagnosis.** Operator presses Snapshot while the relevant terminal window is foreground. App sends exactly that captured image (or selected crop), meeting summary, recent turns, and current issue to vision reasoning. It shows what it observed, distinguishes visible text from inference, and proposes the next safe diagnostic step.

**D. Ad hoc call.** Operator receives a Zoom/Teams 1:1 call without a calendar meeting. Detection proposes the appropriate source. User-initiated Start or a separately enabled trusted auto-start policy begins capture; no dependence on event invitations.

**E. Device switch.** Bluetooth headset disconnects; app displays `Audio source changed / reconnecting`, changes devices, restores processing, and marks any lost transcript interval instead of stitching a false continuous sentence.

---

## 2. Architecture decisions (v1)

### 2.1 Chosen stack

| Area | Baseline | Reason | Verification obligation |
|---|---|---|---|
| Desktop UI / orchestration | C# WPF, `net10.0-windows` (or approved supported LTS fallback) | Native Windows tray, overlays, hotkeys, async pipelines | Check installed SDK and supported OS at project start |
| Native process loopback | Small, separate x64 C++20 Win32/WASAPI helper | Microsoft process-loopback interfaces; avoid pretending ordinary microphone libraries isolate processes | Test sample against actual Zoom/Teams process trees |
| Microphone | Windows WASAPI capture, preferably same native audio helper for common format/timestamps | Independent input; device change notifications | Physical mic test including headphone and speaker modes |
| IPC | Local Windows named pipes with versioned envelopes and per-user ACL; no public TCP listener | Stable C# ↔ native boundary | Crash/restart and access-control tests |
| Live STT | Two independent OpenAI Realtime transcription WebSocket sessions; initially `gpt-live-transcribe` | Streaming deltas and final turns with explicit source labels | Verify schema/events with installed API and live smoke test |
| Turn segmentation | Local VAD (configurable, tested) | Current `gpt-live-transcribe` transcription mode requires **client-side** speech-turn detection | Assert one `commit` per ended speech turn; silence handling |
| Suggestion/vision reasoning | Separate OpenAI Responses API calls to configured model(s) supporting Structured Outputs; vision model for images | Avoid overloading transcription sessions; typed suggestion payloads | Verify model capability, budget and schema with a live test |
| Local state | In-process bounded memory + optional SQLite session metadata after explicit configuration | No default archive/recording; deterministic replay tests | Verify Stop and retention behavior |
| Installer | Self-contained signed Windows package as a later milestone | Usable outside developer machine | Fresh Windows VM install/uninstall test |

**Architectural rule:** The two transcription sessions handle **audio → text only**. They do not receive screenshots and do not generate suggestions. A distinct reasoning service receives the merged transcript context, structured issue state, and optional snapshot. This isolates failure modes and makes the system testable.

### 2.2 Data flow

```text
 Windows: local microphone ──> PCM capture ─> format/resample ─> local VAD ─┐
                                                                           ├─> independent live STT WebSockets
 Windows: selected call app ─> process loopback ─> resample ─> local VAD ───┘                │
                                                                                            v
                                         transcript normalizer <── partial/final turn events
                                                     │
                                      ordered conversation event bus
                                                     │
                      rolling context + issue state + already-tried commands
                                                     │
                              trigger / priority / debouncing engine
                                                     │
                                  typed AI suggestion generator
                                                     │
                                              WPF overlay

 Snapshot button / global hotkey ─> active-window / region capture ─> image +
 timestamp + bounded relevant conversation state ─> vision reasoning ─> overlay
```

### 2.3 Why an app-specific capture path matters

Windows process-loopback capture through `ActivateAudioInterfaceAsync` can include a specified process tree and is not bound to one output endpoint. Microsoft lists **Windows build 20348+** for its ApplicationLoopback sample. Start on Windows 11, check actual OS support at startup, and **prove the chosen PID tree carries the incoming call audio**. Do not assume that selecting `Zoom.exe` or `ms-teams.exe` necessarily finds a media-rendering process; apps may spawn helpers, change renderers, or use browsers. Silence is not proof of connection.

Use endpoint loopback **only** as an explicit fallback, labeled `System output (unisolated)`, because it can capture unrelated playback; never quietly mislabel mixed system sound as Zoom/Teams remote audio. The UI must show the active process/PID and a reliable audio level meter.

### 2.4 Named remote speakers: v1 limitation

`local_mic` identifies the microphone source, **not infallibly the person talking**: loudspeakers may leak remote speech into the mic, and the operator may use another physical microphone. Use echo/correlation filtering and label certainty honestly. `remote_app` gives the mixed incoming call stream; **speaker A vs B** is not guaranteed. Add optional diarization or platform metadata only after measuring accuracy. Do not infer a real name from a transcript guess.

---

## 3. Proposed repository layout

```text
meeting-companion/
├── README.md
├── AGENTS.md                        # agent rules, task interfaces, handoff checklist
├── docs/
│   ├── IMPLEMENTATION_PLAN.md      # this document
│   ├── ADR/                        # architecture decision records
│   ├── supported-platforms.md
│   ├── test-matrix.md
│   └── runbooks/
├── src/
│   ├── MeetingCompanion.Desktop/   # WPF app, tray, overlay, hotkeys
│   ├── MeetingCompanion.Core/      # domain events, session FSM, context, triggers
│   ├── MeetingCompanion.Audio/     # native host client, device state, conversion
│   ├── MeetingCompanion.Stt/       # two-stream transcription adapters
│   ├── MeetingCompanion.Ai/        # reasoning + snapshot analysis adapters
│   ├── MeetingCompanion.Capture/   # GraphicsCapture screenshot implementation
│   ├── MeetingCompanion.Platform/  # Zoom/Teams process discovery / fallback
│   ├── MeetingCompanion.Contracts/ # stable DTOs, JSON schema, IPC protocol
│   └── MeetingCompanion.Native/    # C++ process-loopback and microphone helper
├── tests/
│   ├── Unit/
│   ├── Contract/
│   ├── Integration/
│   ├── Fixtures/                   # synthetic licensed audio and test images
│   ├── PlaybackHarness/            # replay timed, labeled events offline
│   └── WindowsE2E/                # actual device/Zoom/Teams tests (manual+automated)
├── scripts/
│   ├── check-environment.ps1
│   ├── build.ps1
│   ├── test.ps1
│   └── run-dev.ps1
└── .github/workflows/ci.yml
```

No existing repo is implied. If a repository already exists, adapt the layout rather than removing working code or rewriting history. Do not introduce a general-purpose agent framework for the MVP.

---

## 4. Technical contracts and operational invariants

### 4.1 Audio capture and normalization

- Enumerate microphone endpoints and application **audio sessions**; track process tree, PID, executable path, friendly display name, current render activity and selected source. Do not rely on window title alone.
- Capture local microphone independently of incoming playback. Resample both to **PCM16 mono at 24 kHz** for the initially selected STT interface. Downmix with proper scaling, clipping detection, and channel handling; retain source device rate for diagnostics. Format is a negotiated implementation choice, not a Windows capture guarantee.
- Stamp frames using a monotonic clock and attach a clock mapping to UTC. **Do not** sort final turns by service arrival time; normalize by measured source timestamps and explicit turn bounds.
- Use bounded queues, cancellation tokens, cancellation-safe disposal, explicit dropped-frame counters, and health statuses. Never block audio callback threads on API/network calls.
- Provide volume meters for both streams; identify captured silence, no-device, wrong-process, zero-level, permission denied, and disconnected separately.
- Target <= 250 ms local capture/encode queue p95 during normal operation, <= 2 seconds queue hard cap with a visible `gap` event; instrument actual observations before tuning.
- Separate the operator's mic from the **rendered incoming app audio**; do not loop microphone back into speaker output. Test acoustic echo when using external speakers.
- When app restarts or switches device, recreate capture and mark the exact uncertain interval. No concealed drops, busy loops, or endlessly retrying invalid PIDs.

### 4.2 Live STT adapter

- Maintain **two independent authenticated WebSocket transcription sessions**. Each has own session identifier, lifecycle, reconnect policy, audio buffer, VAD, and metrics.
- Configure OpenAI Realtime transcription with `type: transcription`, a model supporting live transcription (`gpt-live-transcribe` initially), input PCM 24 kHz, and `turn_detection: null` (or omitted). Current documentation explicitly says `gpt-live-transcribe` does **not** support `server_vad` or `semantic_vad` in transcription sessions.
- Stream audio using `input_audio_buffer.append`; at local VAD endpoint send `input_audio_buffer.commit` once for the turn. Handle `conversation.item.input_audio_transcription.delta` as provisional and `.completed` as authoritative for that item. Map provider `item_id` to local stream and turn ID.
- Use local VAD with start/end thresholds, silence padding and maximum turn duration. Avoid committing every 20 ms or holding a multi-minute speech turn until call termination. Explicitly test overlapping local/remote speech.
- Retry websocket connection with bounded exponential backoff + jitter; do **not** replay already sent/possibly processed audio without a well-defined dedupe protocol. On uncertain gaps: mark transcript as incomplete; resume fresh capture.
- Distinguish `partial`, `final`, `gap`, `error`, `source_changed` events; never rewrite accepted final turns because a stale delta arrived.
- Cap streams and token/cost usage; audio-only mode may work without model reasoning if suggestion provider is unavailable.

### 4.3 Shared event schema (versioned)

The following JSON is an **internal application contract**, not a claimed OpenAI endpoint payload:

```json
{
  "schema_version": 1,
  "event_id": "uuid",
  "session_id": "uuid",
  "stream_id": "local_mic",
  "event_type": "transcript_final",
  "turn_id": "uuid",
  "provider_item_id": "string-or-null",
  "captured_start_ms": 51420,
  "captured_end_ms": 54760,
  "received_utc": "2026-10-09T14:10:15Z",
  "text": "Could you check the service monitor?",
  "attribution": "YOU",
  "attribution_confidence": "source_confirmed",
  "confidence_note": null
}
```

Required enum sets: `stream_id = local_mic | remote_app | endpoint_fallback`; `event_type = transcript_partial | transcript_final | audio_gap | source_changed | snapshot_added | trigger_detected | suggestion_generated | session_state`; `attribution = YOU | REMOTE | MIXED | UNKNOWN`; `attribution_confidence = source_confirmed | inferred | unknown`. Never claim names from these fields. Contract changes require version bump or backward-compatible additions + tests.

### 4.4 Conversation state and context

- Use a bounded ring buffer containing recent finalized turns (start with a configurable **10-minute transcript window**, memory only), plus an incremental session summary for older context. This is a design default, not an audio recording.
- Track `current_topic`, `open_question`, `reported_error`, `observed_environment`, `hypotheses`, `confirmed_facts`, `attempted_steps`, `failed_steps`, `suggested_steps`, `unresolved_questions`, `last_snapshot_id`, `speaker_turns`.
- Attribute every fact to evidence: `transcript` with turn IDs, `snapshot` with timestamp/crop, `tool_result` if later supported, or `assumption` if unverified.
- Resolve out-of-order transcript completions; track overlapping speech rather than merging a single invented sentence.
- On user Stop, release buffers and sessions. Default is **no audio/screenshot archive**; debug trace and transcript export are opt-in, clearly labeled. Minimal error metrics may persist without transcript contents.
- Keep model context compact: last relevant turns + summary + active issue + one latest screenshot + short history of attempts. Never resend an entire call on every new suggestion.

### 4.5 Trigger engine and policies

Triggers: `direct_question`, `technical_blocker`, `request_for_solution`, `specific_error`, `snapshot_requested`, `manual_ask`. Apply a deterministic priority and minimum confidence before generation.

Suggested flow:

```text
final / stable partial transcript
   -> local candidate detector (keywords, punctuation, turn-taking, direct address)
   -> cross-stream conversation classifier with short context
   -> priority & suppression (is user already answering? already suggested? cooldown?)
   -> typed reasoning request
   -> validated suggestion
   -> non-disruptive overlay card
```

- Favor a likely **direct question** over casual discussion. A generic "what should we do?" in a group must not automatically be treated as directed at the operator.
- Use cautious early generation from stable partials only if enabled; cancel stale pending suggestions on corrections/new turns. Do not produce the same card each transcription update.
- A new screenshot/manual ask overrides automatic prioritization; when both are active, cancel or merge to prevent duplicate bills and contradictory advice.
- Proposed modes: **Quiet** (manual/urgent only), **Balanced** (default; confident triggers), **Active** (more suggestions). All modes need a cooldown, duplicate suppression and maximum concurrent reasoning calls.
- Track precision/recall on labeled fixtures; optimize for useful advice, not maximum number of suggestions.

### 4.6 AI suggestion response contract

Use a Responses API model compatible with **Structured Outputs**. The runtime must validate both JSON schema and semantics, and handle refusals/timeouts/empty/incomplete outputs. Example app-level output:

```json
{
  "suggestion_id": "uuid",
  "kind": "suggested_reply",
  "priority": "normal",
  "headline": "Explain failover without overcommitting",
  "body": "We should verify which service owns alerts after failover and confirm recovery behavior before promising uninterrupted delivery.",
  "commands": [],
  "what_to_look_for": [],
  "confidence": "moderate",
  "evidence_event_ids": ["turn-event-id"],
  "assumptions": ["Failover setup not yet verified"],
  "expires_at_ms": 79230
}
```

Schema rules: `kind = suggested_reply | diagnostic_step | context_clarification | screenshot_analysis`; `priority = low | normal | high`; `confidence = low | moderate | high`; `commands` are **display-only** arrays with risk level (`read_only`, `modifying`, `unknown`); initial auto-suggestions must be `read_only` or descriptive. Require evidence references for factual claims; distinguish **"visible on screenshot"**, **"stated in call"**, and **"hypothesis"**. Unknown identifiers must not become fabricated commands. Set hard caps for output length, generation time, and retries.

### 4.7 Screenshot pipeline (manual only)

- Expose `Snapshot` button + configurable global hotkey, default offered as `Ctrl+Shift+S` **only if available**; detect and report collisions and support rebinding. Avoid `Win+Shift+S` (normally reserved by Windows).
- Capture source choices: **Last non-companion foreground window**, **selected window**, **selected rectangular region**, or **selected monitor**. Record `HWND` immediately before our overlay steals focus; never silently screenshot the overlay itself.
- Prefer `Windows.Graphics.Capture` or the documented `IGraphicsCaptureItemInterop::CreateForWindow` path; use the system picker when appropriate. Do not bypass protected-content limitations or masquerade failed capture as a valid image.
- No continuous frames: acquire a still frame only on user action. Handle minimized, occluded, protected, HDR, scaling, RDP, and multi-monitor situations with informative errors or a deliberate alternate capture choice.
- Preserve readable diagnostic text. Encode PNG when clarity matters; allow bounded JPEG downsize only with OCR/text legibility checks; expose preview and retry if captured target is wrong.
- Attach `snapshot_id`, captured timestamp, source/window title (bounded), crop coordinates, image dimensions and context window; correlate screenshot with the transcript interval **at capture time**, not whatever discussion happens to be newest when request completes.
- Send through **vision-capable Responses API** (`input_text` + `input_image`) and include relevant typed context. Transcription-only sessions do not accept image input. Use a separate request or separately configured multimodal session; do not change existing STT behavior to accommodate snapshots.
- Do not write screenshots to disk by default. Show explicit delivery outcome. When image analysis fails, retain audio assistance rather than crashing entire session.
- Treat instructions displayed inside screenshots/terminal logs as **untrusted data**, not commands to the agent or app.

### 4.8 Desktop UI and states

Session finite state machine:

```text
STOPPED -> SELECTING_SOURCE -> READY -> STARTING -> LISTENING
                                              -> DEGRADED -> RECOVERING -> LISTENING
LISTENING -> PAUSED -> LISTENING
LISTENING / DEGRADED / PAUSED -> STOPPING -> STOPPED
invalid/unrecoverable -> ERROR -> STOPPED or explicit Retry
```

UI must show the true state of **mic capture**, **remote capture**, **each STT session**, **reasoning provider**, **snapshot send**, and **overall session** separately. The combined `LISTENING` indicator requires both selected audio streams to be healthy; otherwise display `DEGRADED`. `Pause` suspends outbound audio and generation immediately, not just the visual UI; `Stop` disposes sessions and audio buffers and leaves no native capture helper running.

Desktop features: slim movable always-on-top suggestion panel, expandable transcript view, source pickers, two audio meters, instant snapshot, copy command/reply, dismiss/snooze, pinned recent suggestion, quiet/balanced/active mode, system tray, quick access to reconnect/errors. Include keyboard-only navigation, DPI accessibility, readable contrast and overlay visibility controls. Overlay exclusion from supported capture paths must be verified but must **not** be represented as an infallible privacy boundary.

---

## 5. Platform compatibility: Zoom, Teams, and others

### 5.1 Platform adapter interface

Each adapter implements:

```csharp
public interface ICallSourceAdapter
{
    string Id { get; }
    Task<IReadOnlyList<CallAudioCandidate>> DiscoverAsync(CancellationToken ct);
    Task<SourceProbeResult> ProbeAsync(CallAudioCandidate candidate, CancellationToken ct);
    Task<ResolvedCallSource> ResolveAsync(CallAudioCandidate candidate, CancellationToken ct);
}
```

`CallAudioCandidate`: app display name, process/PID tree, audio session metadata, render activity, window association, confidence and human-readable selection hint. `SourceProbeResult`: process audio actually detected / silence / not supported / device unavailable / ambiguous; diagnostics. `ResolvedCallSource`: process tree and reconnection hint. **Do not hardcode a single PID forever.** A process restart requires rediscovery.

- **Zoom native:** identify live Zoom process tree/audio sessions, validate meeting and unscheduled 1:1 calls. Test transition waiting room → call → hold → resume → leave and device switches.
- **Teams native:** discover the current Teams app and media child process; do not assume that only the parent executable produces audio. Test scheduled meeting, ad-hoc call, call transfer, minimized app, and update/restart.
- **Browser-based Zoom/Teams:** detect browser process; show `Browser audio — other tabs may be included` if true. Verify profile/process isolation rather than promising tab-specific capture. Offer application/system audio fallback.
- **Other clients:** choose actual audio-rendering session/PID through picker, resolve same pipeline. Never demand a scheduled meeting.
- **Fallback:** explicit full-endpoint WASAPI loopback, selected output device, **prominent unisolated status**; avoid recording unrelated system sounds by implication.

### 5.2 Compatibility matrix (must run on actual Windows devices)

| Scenario | Mic/remote separate | Switching recovery | Snapshot | Required result |
|---|---|---|---|---|
| Zoom desktop meeting | Yes | Yes | Yes | Both correctly labeled, no ghost audio |
| Zoom desktop 1:1 call | Yes | Yes | Yes | Works without calendar event |
| Teams desktop meeting | Yes | Yes | Yes | Mixed remote audio reliably discovered |
| Teams desktop direct call | Yes | Yes | Yes | Source detected, mic separate |
| Zoom/Teams app update/restart | Recovery | Yes | After recovery | No stale PID or false green state |
| USB headset | Yes | Yes | Yes | Input/output independently selectable |
| Bluetooth headset/handset profile transition | Verify and document | Yes | Yes | No silent wrong-device listening |
| Built-in speakers causing mic echo | Mitigated/labeled | N/A | Yes | Remote voice not confidently mislabeled YOU |
| Call and unrelated media simultaneously | Isolated or clear fallback label | N/A | Yes | No wrong attribution |
| Browser Teams / Zoom | Conditional | Yes | Yes | Limits clearly surfaced |
| App with no process capture support | Fallback only | Yes | Yes | No fictitious process isolation |
| Two monitors / scaling / RDP | N/A | N/A | Yes, when supported | Correct target and captured dimensions |
| Network drops mid-question | Capture remains; STT degraded | Yes | queued/fails visibly | Gap marked, no ghost response |
| Model rate limit / no credit | Capture/transcripts may continue | Recovery | clear failure | No retry storm |

A green check requires a reproducible test result with environment details, logs **without secrets**, and an observed transcript/audio/source mapping. Do not equate automated mock success with real Zoom/Teams support.

---

## 6. Latency, reliability, cost and observability targets

**Targets are acceptance goals to measure, not promises about external model or network speed.**

| Metric | Initial goal | Measurement |
|---|---|---|
| Time from end of a clear directed question to first useful suggested reply | p50 <= 3 s; p95 <= 6 s on reference test setup | Monotonic event timestamps; 30+ labeled cases |
| Time from Snapshot shortcut to first visible analysis | p50 <= 5 s, p95 <= 10 s, network-dependent | Capture-to-card telemetry on 20+ screenshots |
| Local audio pipeline queue age | p95 <= 250 ms | Per-stream `oldest_frame_age_ms` |
| A user-visible health state after remote stream failure | <= 2 s | Kill helper or mute process, observe UI |
| Start/Stop cleanup | Stop <= 2 s locally, no residual mic/process capture | Device-level inspection + clean process exit |
| Duplicate suggestion rate | <= 5% on labeled replay set | Same problem/suggestion fingerprint tracking |
| Correct source attribution | >= 99% on controlled headphones test; separately measure speaker-echo cases | Confusion matrix YOU vs REMOTE vs UNKNOWN |
| Crash-free overnight run | 8 h idle/active mixed test, no stuck capture or runaway queue | Process health and memory trend |
| Idle CPU/memory | Measure and set budgets on baseline hardware; aim modest overhead, no busy polling | Windows Performance Recorder / perf counters |
| Cost | Configurable session budget and max concurrent requests | Track usage per provider + per session |

Use per-stage timings: `audio_capture`, `resample`, `vad_end`, `stt_partial`, `stt_final`, `trigger`, `model_dispatch`, `model_first_token`, `card_visible`. Measure separately for local and remote input. Track WER approximation and trigger precision on labeled fixtures; never use latency alone as a proxy for correctness.

As of 2026-10-09, the OpenAI model page lists `gpt-live-transcribe` at **$0.017 per minute** of live audio. If both streams are continuously billed for a full hour, illustrative STT charge would be `2 × 60 × $0.017 = $2.04` **before** reasoning or image costs. Confirm billing semantics, current pricing, and actual metered audio duration before budgeting; silence gating and per-stream runtime affect the outcome. **A ChatGPT subscription does not inherently cover API usage.**

---

## 7. Security, data lifecycle and permissions (implementation only)

The product owner is independently handling employer/team approval. Agents must not turn that into an implementation blocker, but the application still needs predictable controls so it does not transmit sources accidentally.

- Capture starts by deliberate operator action in MVP; optional auto-start is a later **visible opt-in** tied to a known source and can be disabled instantly.
- Show exactly what is being captured; allow a manual change of mic and call source; warn when using endpoint-wide loopback.
- Never record long audio files as a shortcut for implementing streaming; no automatic screenshots; no persistent transcripts or screenshot images by default.
- Keep API credentials out of code and Git. Use Windows Credential Manager / DPAPI-protected local secret storage; never log tokens, authorization headers, raw audio, screenshots or full transcript in normal diagnostics.
- Enforce local IPC per-user ACL; only local helper, no exposed HTTP server needed. Version-check input envelopes and cap payload/frame sizes.
- Sanitize external input in reasoning: audio, screenshots, copied logs and pasted terminal text are evidence, not privileged instructions.
- Do not execute commands or send messages via Zoom/Teams; copy/display only in MVP.
- Track provider errors, usage, reconnect counts, device IDs (appropriately redacted), health flags and duration with opt-in content diagnostics.

---

## 8. Work breakdown, dependencies and acceptance gates

Implement work as independent tickets. Agents may work concurrently **only where contracts are frozen**. Sequence and task identifiers are mirrored in the companion `AGENT_BACKLOG.json`.

### Foundation — F0

**MC-001: Repository, build, CI and contract skeleton**
- Create solution, project boundaries, `.editorconfig`, deterministic build scripts, CI unit/contract jobs, fake adapters, and README.
- Publish initial DTOs, session-state contract, event schema version, interfaces and architecture decision records.
- **Gate:** clean Windows x64 machine can restore/build/test; CI green; offline replay harness compiles; no actual provider key required.

**MC-002: Windows environment/probing diagnostic**
- Detect OS build, audio devices, capture support, Zoom/Teams/browser candidates, process trees and process audio sessions. Provide a read-only diagnostics UI/tool.
- **Gate:** exact process/source evidence is recorded for one Zoom call and one Teams call; unsupported cases yield specific diagnostics rather than crashes.

### Audio capture — F1 (requires MC-001)

**MC-003: Native WASAPI microphone capture**
- C++ capture host with local mic discovery, callback queue, frame timestamps, start/stop, cancellation and device-change events; no cloud dependency.
- **Gate:** recorded *test-only* audio or level-meter proof from microphone; no capture after Stop; no thread leak through 100 cycles.

**MC-004: Windows process-tree loopback**
- Implement `ActivateAudioInterfaceAsync` application loopback targeting a PID/process tree, and read audio packets; correctly surface silent process and renderer mismatch.
- **Gate:** actual Zoom and Teams remote speech detected while separate music player is excluded; 1:1 and meeting checks run; silence/unsupported paths diagnosed.

**MC-005: Dual-stream normalization, IPC and session lifecycle**
- Versioned local named-pipe protocol, conversion to supported PCM, stream identifiers, monotonic time alignment, bounded queues, meters, recovery and pause/stop.
- **Gate:** synthetic overlapping streams never swap labels; helper crash degrades UI within 2 s; 100 start/stop cycles leave zero capture handles.

### Live speech to text — F2 (requires MC-005)

**MC-006: Dual Realtime transcription transport**
- Implement independent STT WebSockets, local VAD, append/commit protocol, partial/final mapping, rate-limit handling, reconnect and gap events.
- **Gate:** controlled two-speaker headset test produces separate YOU/REMOTE transcripts; forced disconnect shows a gap and recovers; no duplicate final turns.

**MC-007: Ordered conversation and issue state**
- Event bus, transcript reorder, rolling memory buffer, summary logic, evidence references, attempts/hypotheses state and replay fixtures.
- **Gate:** deterministic replay results even when STT completion order changes; overlapping audio and gap indicators remain honest; Stop clears default content state.

### Screenshots — F3 (requires MC-001 and MC-007 contracts; work can parallelize after skeleton)

**MC-008: One-click still capture and shortcut**
- Window/region/monitor picker, hotkey conflict detection, foreground window selection, DPI/HDR/blank image errors, overlay exclusion where supported.
- **Gate:** exact targeted window/region captured across two displays and scaling factors, including when hotkey is used while overlay has focus; no continuous capture.

**MC-009: Snapshot reasoning bridge**
- Vision request with image + captured-at transcript context, structured result, image readability checks, bounded payload, usage tracing and no default image persistence.
- **Gate:** screenshot of a known fabricated test error leads to a diagnosis tied to visible text; text-only fallback remains alive on image failure; unrelated transcript turn is not used.

### Proactive reasoning — F4 (requires MC-006 and MC-007; snapshots optionally MC-009)

**MC-010: Trigger classifier and deduper**
- Direct question, support request and technical blocker candidates; quiet/balanced/active policies, cancellation, cooldown, relevance scoring, priority arbitration.
- **Gate:** labeled replay fixture accuracy measured; repeated partials do not cause repeated suggestions; ambiguous group questions are appropriately restrained.

**MC-011: Typed suggestion generation**
- Typed Responses API contract, answer-versus-command style, grounding policy, assumptions, evidence pointers, command risk classification, timeouts and cancellation.
- **Gate:** 30 labeled transcript scenarios yield schema-valid suggestions, no invented observed errors; unknown namespace/pod is not fabricated; read-only commands only for auto suggestions.

### UI and integration — F5 (requires contracts early; end-to-end gating requires MC-003..011)

**MC-012: Desktop shell, overlay, tray and controls**
- Source selection, levels and health, Start/Pause/Stop, transcript snippets, suggestion cards, Snapshot and copy actions, keyboard navigation.
- **Gate:** hotkeys, lifecycle controls, no false-green status, copy doesn't modify other apps, app exits cleanly without helper leak.

**MC-013: Zoom adapter and real-call acceptance**
- App identification, process-tree probing, 1:1/meeting/BT audio tests, restarting app, input/output device changes, screenshots during calls.
- **Gate:** Zoom meeting + ad-hoc call pass actual hardware matrix, evidence saved, observed limitations documented.

**MC-014: Teams adapter and real-call acceptance**
- Modern Teams process/audio topology probing, direct call + scheduled meeting, app update/relaunch recovery, process isolation and screenshot tests.
- **Gate:** Teams meeting + ad-hoc call pass actual hardware matrix, evidence saved, observed limitations documented.

**MC-015: Generic app and browser fallback**
- Manual audio-session selection, endpoint loopback fallback with obvious unisolated label, browser limitations, proper device routing.
- **Gate:** unsupported app is handled without crashing and without pretending tab/app isolation.

### Hardening and release — F6 (after integration)

**MC-016: Instrumentation, budgets and fault injection**
- Per-stage timing, errors without content leakage, model quotas, caps, cancellation, slow model, lost network, device unplug and helper crash handling.
- **Gate:** benchmark report includes median/p95 latency and cost; repeated failures do not spin or produce runaway model requests.

**MC-017: Regression, acceptance suite and Windows packaging**
- CI suite, clean Windows install, driver and OS compatibility, signed package strategy, upgrade/uninstall, QA checklist, runbooks.
- **Gate:** all launch-blocking items below pass; release artifacts reproducible and verification recorded.

### 8.1 Dependency and parallel work plan

```text
MC-001 ──┬── MC-002 ───────┬──────────── MC-013 (Zoom)
         ├── MC-003 ─┐    └──────────── MC-014 (Teams)
         ├── MC-004 ─┼─> MC-005 -> MC-006 -> MC-007 -> MC-010 -> MC-011 ─┐
         ├── MC-008 ────────────────────> MC-009 ────────────────────────┤
         └── MC-012 (UI shell, contracts first) ─────────────────────────┤
                                                             MC-015 ────┤
                                                                      MC-016 -> MC-017
```

Contract-first ownership: `MC-001` owns versioned interfaces; native agent owns `MC-003–005` native side; STT agent owns `MC-006`; reasoning agent owns `MC-007/010/011`; vision agent owns `MC-008/009`; UI agent owns `MC-012`; platform validation agent owns `MC-002/013/014/015`; QA agent owns `MC-016/017`. After freeze, agents should not independently alter shared schemas.

### 8.2 Release-blocking checklist

- [ ] Proved both streams in Zoom **meeting and 1:1** with correctly labeled local/remote transcript.
- [ ] Proved both streams in Teams **meeting and 1:1** with correctly labeled local/remote transcript.
- [ ] Screenshot button and hotkey capture the intended window or region and integrate with current question state.
- [ ] At least 30 question/problem replay cases evaluated, with false-positive and accuracy metrics published.
- [ ] No unsolicited speech, command execution, microphone routing changes, meeting joins, recordings, or automated screenshots.
- [ ] Device/source loss shows degraded state and can be recovered without app restart in supported cases.
- [ ] All provider/network failures are bounded, logged without secrets and reflected in UI.
- [ ] Stop and process exit demonstrably release audio device handles, native helper, sockets and memory buffers.
- [ ] Offline tests pass without access to paid AI APIs; live integration suite guarded by explicit opt-in.
- [ ] Known unsupported audio modes explicitly documented with their fallbacks; no unverified “works everywhere” claim.

---

## 9. Agent execution protocol (non-negotiable)

**On every task:**

1. Read this plan, `AGENTS.md`, relevant source and existing tests; inspect current Git branch/status and accepted `main` before editing. Never treat older chat summaries as source-of-truth for repository state.
2. Create a narrowly scoped branch, e.g. `feat/mc-006-dual-stt`. Do not reset/rebase/force-push/delete or rewrite unrelated history. Do not overwrite another agent's work. Ask for coordination only if unavoidable; otherwise take the safest independent work package.
3. Write a 5–10 line task-specific implementation note: exact interfaces, files touched, test fixtures, platform assumptions, and known risks.
4. Implement behind contracts and capability flags where appropriate; preserve functioning code paths. Include tests for success, wrong input, timeouts, cancellation, and repeated start/stop.
5. Use **offline fake providers and audio fixtures** as default tests. Never require production credentials or live Zoom/Teams calls in ordinary PR CI.
6. Run repository formatter, compilation, unit/contract tests, and targeted integration harness; report the **actual commands and results**, not predicted success.
7. Make an evidence-bearing PR with: summary, files, risk, tests, logs, screenshots where appropriate, backward compatibility, known limitations and manual acceptance steps. Link task ID and any ADR.
8. Never merge solely because unit tests pass: platform tasks require observed real Zoom/Teams checks and explicit release-gate review.
9. If blocked by access to Windows devices, production meeting applications or credentials, complete the offline-safe parts; label the missing tests `NOT VERIFIED`, rather than fabricating results.
10. Handoff with **Done / Verified / Open / Next safest action**. No ambiguous “complete” claims.

### 9.1 Suggested agent team

| Agent | Scope | May edit | Must not edit without coordination |
|---|---|---|---|
| Architect / contracts | MC-001, ADRs, schemas, gatekeeping | Core/contracts/CI | Native implementation, UI feature implementation |
| Native Windows audio | MC-003..005 | Native/audio + audio tests | AI contracts, screenshot, UI design |
| STT/event pipeline | MC-006..007 | STT/core ingest + fixtures | Native COM implementation, UI |
| Reasoning/quality | MC-010..011 | Trigger/AI modules + evals | IPC, audio capture |
| Snapshot/vision | MC-008..009 | Capture/AI-vision adapter + tests | Live STT streaming internals |
| Desktop UX | MC-012 | Desktop and presentation tests | Core schema without approval |
| Platform/QA | MC-002, MC-013..017 | Adapters, harness, docs, packaging | Shared contract breaking changes |

### 9.2 Agent handoff template

```md
## Task ID
## Starting state: repo, branch, HEAD, working-tree status
## Scope changed: files/modules
## Implementation summary
## Tests run and exact results
## Manual test results (Zoom/Teams version, Windows build, audio device)
## Measured latency or resource evidence (if applicable)
## Security / cost / regression considerations
## Known limitations and NOT VERIFIED items
## PR or commit reference (if any)
## Next safest action
```

### 9.3 Launch prompt to delegate to any AI coding agent

> You are implementing an assigned `MC-###` task in **Real-Time Meeting Companion**. Read `docs/IMPLEMENTATION_PLAN.md` and `AGENTS.md` before editing. Inspect the current repository state and related tests. Implement only your assigned ownership scope, preserve accepted `main` and working features, produce reproducible tests and failure-path handling, and avoid unauthorized commits/merges unless the owner provides repository permissions. Do not invent API support or validation results; distinguish mock tests from physical Zoom/Teams proof. Return the exact handoff template in §9.2.

---

## 10. Verification fixtures and test methodology

### 10.1 Offline synthetic fixtures

Prepare *synthetic or authorized* paired audio streams with reference labels (separate 24 kHz PCM WAV files with clock annotations, for testing only), plus event-order perturbations. Include:
- local question vs remote question; direct address vs generic discussion;
- overlapping speech, music/notification contamination, mic speaker echo;
- several different audio levels, silence, device reconnect gaps;
- partial transcript corrections, delayed `.completed`, duplicate delta, closed connection mid-turn;
- question already answered by operator; repeated request; technical task with already-attempted commands;
- fake screenshot containing a known, readable error, but with malicious-looking instructions in the image to test injection resistance.

The replay harness must support deterministic virtual time and injectable AI/stt responses, and must output confusion matrix, trigger precision/recall, duplicate count, latency distribution, and capture state transitions. Fixture labels are not live privacy approval requirements; agents must build fixtures without sensitive real meetings.

### 10.2 Windows hardware smoke tests

Run on at least one supported Windows 11 x64 workstation and, before declaring release-ready, verify with a second audio-device configuration. Preserve: Windows OS build, Zoom/Teams version, microphone and output device, process tree evidence, whether a headset or speakers were used, timestamped source health, test outcome, and failure logs. No false green results.

### 10.3 Example spoken technical troubleshooting cases

**Case A: Monitoring:** Remote says “Prometheus can't discover our exporter, but the pod is running.” Expected: ask for or suggest checking ServiceMonitor selectors and Prometheus target discovery; do not invent existing service names or assert firewall issue without evidence.

**Case B: Kubernetes:** Remote says “The pod keeps restarting.” Expected: separate crash exit, OOM kill, liveness failures, and deployment rollout; propose read-only examination such as `kubectl describe pod <pod> -n <namespace>` only as an explicit **template**, never as a command with fictional concrete identifiers.

**Case C: CI:** Remote says “Our GitHub checks keep timing out.” Expected: request workflow/run identifier, identify slow job and timeout reason, avoid suggesting destructive branch changes or force pushes.

**Case D: Screenshot:** Image contains an error code that differs from the spoken guess. Expected: cite visible code, explicitly distinguish spoken interpretation, and avoid inventing surrounding logs.

**Case E: Call context:** User has already said a particular command was executed. Expected: no repeat of the same step without explaining why rerunning would be useful.

---

## 11. Engineering risks and mitigations

| Risk | Impact | Mitigation / release gate |
|---|---|---|
| Zoom/Teams renderer outside selected PID tree | Incoming speech missing | Audio session enumeration, live level probes, PID rediscovery, explicit selection, real-app tests |
| Mic receives remote voice via loudspeaker | Remote misclassified as YOU | Headset recommended; echo correlation/AEC; ambiguous `MIXED`/`UNKNOWN`; dedicated echo tests |
| Multiple remote speakers blended | Wrong individual speaker assignment | Label `REMOTE` only; diarization and platform metadata deferred |
| Browser tab isolation unavailable | Capture other browser audio | Display exact isolation limit; allow browser-profile/window choice and endpoint fallback |
| Model provider changes API event shape | Broken transcript or suggestions | Version adapters, pinned known-compatible model config, integration smoke test, schema validation |
| Transcription final delayed/out of order | Misattributed or stale response | Monotonic capture clock, turn IDs, bounded reorder window, stale-result suppression |
| Reconnect loses text / duplicate model charges | Bad continuity / cost | Explicit gap events; bounded retry, no blind audio replay, request idempotency where applicable |
| Too many proactive comments | Distracting | Priorities, modes, cooldown, dedupe, user feedback and evaluations |
| Incorrect commands in technical call | Operational risk | Read-only display by default, unknown variables marked, assumptions visible, no execution |
| Screenshot captures overlay/wrong screen | Wrong visual context | Preserve foreground HWND; source preview; capture source label; failure prompt |
| Audio or image stored inadvertently | Unexpected retention | No default file writes; explicit opt-in export; test for temp files and log contents |
| Windows COM/audio callback deadlock | Hung app and unusable microphone | Dedicated capture threads, timeouts, cancellation, native watchdog and stop/start soak tests |
| API spend continues when paused | Unexpected cost | Pause halts audio sends and generations; usage counters, session cap, enforce billing budget |
| Optimistic health state | User thinks assistant is listening when it isn't | Separate capture/STT states, health TTL, synthetic fault injection |

---

## 12. First two build iterations (smallest proof that matters)

### Iteration 0: Feasibility spikes (no vendor API bills)

1. Create `MC-001` repository/contracts and `MC-002` read-only probe.
2. Build a native proof-of-concept showing **mic level meter** and **isolated Zoom/Teams app level meter** simultaneously, with operator-labeled synthetic call source. Verify that playing unrelated media does not appear in call-app capture.
3. Test Zoom 1:1 and Teams 1:1, plus one actual meeting each. Record process IDs, actual audio sessions, system build, device, and failures. If capture topology differs, update ADR *before* implementing STT.
4. Add local audio test buffers/VAD with timestamps, not recordings by default.

**Stop/go gate:** If native app-specific capture cannot be demonstrated for either priority app, investigate its renderer process topology and document fallback. Do not claim “seamless” or move the product to release status based on a fake audio adapter.

### Iteration 1: First useful end-to-end demo

1. Connect two live STT WebSockets with independent local VAD and visible real-time transcript labels.
2. Implement one manual **Ask/Suggest** action that sends current conversation context to typed reasoning and displays a written suggestion. Do **not** make trigger automation a prerequisite for the first demo.
3. Implement one-click **Snapshot active window** into a vision request with same session context; display analysis next to a transcript excerpt.
4. Implement Stop and Pause that actually terminate/stop capture and provider requests, and test network/device failure.
5. Demo: join Zoom/Teams, ask a simple question aloud, receive labeled transcripts, press Snapshot on a deliberately generated test error, receive a useful read-only next step without typing.

**Next after demo:** add automatic trigger detection and full platform matrix. Prioritize reliability over large interface features or third-party app integrations.

---

## 13. Primary documentation references (verified 2026-10-09)

These are vendor documentation and sample links, **not proof of compatibility in the user's environment**. Agents should re-check them before implementing a vendor-specific adapter.

1. Microsoft, ApplicationLoopback process-tree WASAPI sample, OS build requirement and process inclusion: https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/
2. Microsoft, Windows.Graphics.Capture, snapshot and picker lifecycle: https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture
3. Microsoft, `CreateForWindow` Win32 capture interop: https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow
4. Microsoft, WASAPI capture packets: https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream
5. OpenAI, Realtime transcription and `gpt-live-transcribe` client-side VAD/commit: https://developers.openai.com/api/docs/guides/realtime-transcription
6. OpenAI, `gpt-live-transcribe` supported modalities and pricing: https://developers.openai.com/api/docs/models/gpt-live-transcribe
7. OpenAI, Responses API image input: https://developers.openai.com/api/docs/guides/images-vision
8. OpenAI, Responses API Structured Outputs: https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses
9. OpenAI, Realtime conversational image path (alternative to Responses, not the STT session): https://developers.openai.com/api/docs/guides/realtime-conversations

---

## 14. Definition of success

An operator starts Meeting Companion on Windows, joins a Zoom or Teams meeting or direct call, and sees independently healthy microphone and incoming audio sources. Two live transcripts correctly distinguish local from remote speech. A teammate asks a direct question or encounters a technical issue; a concise and evidence-aware suggestion appears, without manual prompting, within the measured target. The operator presses Snapshot on an on-screen error; a targeted analysis that uses both the picture and relevant call context appears. The operator can pause and stop immediately. Device swaps, dropped connections and application restarts have clear status, bounded recovery and no hidden capture. Every green compatibility claim is backed by measured, repeatable evidence.

**Do not equate a working demo, green mock tests, or this plan with a completed product.**
