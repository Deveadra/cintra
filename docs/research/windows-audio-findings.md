# Windows audio feasibility — Agent B, 2026-10-09

## Conclusion and scope

**The Windows capture primitives are feasible on this workstation; reliable Zoom/Teams call separation is not yet established.** Actual native experiments obtained independent microphone and selected-process loopback packets and demonstrated inclusion/exclusion of a synthetic renderer. No Zoom or Teams call was joined, no remote speech was tested, and no application-specific audio renderer topology was observed. MC-002/003/004 production gates remain open.

All code and documents are confined to `spikes/windows-audio/` and `docs/research/`. Branch: `orion/agent-b-windows-audio-spike`; starting HEAD `7365340b66f45468a3a0cedd35a9888367bee7d9`. No shared contracts, solution, CI, backlog, main, or other applications were edited. No commit, PR, push or merge was created. The standalone .NET interop implementation is an experiment, not a proposal to replace the production C++20 helper.

## Actual environment observations

| Item | Observed result |
|---|---|
| OS | CIM: Microsoft Windows 11 Home, x64, 10.0.26200; registry: 25H2, UBR 9457 |
| OS reporting caveat | Registry ProductName said Windows 10 Home; CIM confirmed Windows 11. Do not use that registry label alone. |
| .NET | SDK 10.0.401, x64 runtime 10.0.12; builds succeeded |
| C++/SDK | No `cl`, `clang`, `clang-cl`, `g++`, `cmake`, or `clang-format` on PATH; conventional VS Installer/vswhere and Windows Kits Include/registry locations absent. No C++ compilation attempted; nonstandard installs not exhaustively searched. |
| Audio hardware inventory | Realtek Audio and AMD High Definition Audio Device, both CIM Status OK |
| Active render endpoint | Speakers (Realtek(R) Audio), index 0 in this snapshot |
| Active capture endpoint | Microphone (Realtek(R) Audio), index 0 in this snapshot |
| Physical configuration | Endpoint metadata observed; headset vs built-in/external speakers, room acoustics, driver version and actual human input not established |
| Zoom | Installed executable FileVersion 7.1.9.48550; not running in snapshots |
| Teams | Per-user MSTeams package 26260.1701.5139.3736; not running in snapshots |
| Session inventory | Outside sandbox: two inactive render sessions, one system-sounds/multi-process and one unrelated application. No call-app session. No unrelated audio was captured. |
| Restrictions | Sandbox denied CIM/Appx queries and audio-session enumeration; read-only checks outside sandbox succeeded. Endpoint enumeration and short capture worked in sandbox. |

No host name, account identifiers, endpoint GUIDs, meeting identifiers, or participant names are retained in this report. Real process IDs in local diagnostics were ephemeral; the matrix uses aliases instead.

## Actual Windows experiments

1. **Independent streams, three-second request:** self-process loopback returned 287 packets / 126,567 frames, stereo PCM16 at 44.1 kHz; mic returned 135 packets / 64,800 frames, stereo float32 at 48 kHz. Both stopped with result 0. Mic peak 0.0006587618263438344, RMS 0.00005797098840678469; one initial discontinuity. This proves packets/levels, not speech, voice identity, synchronization quality, or acoustic separation.
2. **Silent process behavior:** self-process peak 0.000030517578125 (one PCM16 LSB), RMS about 0.00001462 despite no renderer intentionally started by that process. Consistent with low-level conversion noise/dither; its cause was not independently proven. Exact zero is unsuitable as the only wrong-source detector.
3. **Synthetic renderer inclusion:** an eight-second, 440 Hz, amplitude 0.03 fixture was selected by its live PID. Three-second capture peak 0.030059814453125, overall RMS 0.018409804477411328, 165 packets. A steady 500 ms window had RMS 0.021190256415765586. Fixture and capture exited 0.
4. **Unrelated renderer exclusion:** while that same fixture continued, a new self-process capture yielded peak 0.000030517578125, RMS 0.000014622413925536946, 291 packets. This supports process isolation for this synthetic topology on this endpoint. It does not prove Zoom/Teams isolation or browser-tab isolation.
5. **Timing failure retained as evidence:** the first scripted rerun did not reach the selected-tone threshold. A capture meter was delayed to 3,743 ms with packet age 3,205 ms; selected peak remained one LSB. The initial script did not wait for render readiness. The fixture now emits `tone_ready` after its first buffer submission and the harness waits for it. This fixes the test's startup ordering; it does not establish a Windows latency guarantee or explain every stall.
6. **Invalid PID:** 2,147,483,647 was not running; explicit error, zero packets and exit 2, no fallback.

The final reproducible harness results and remaining gaps are recorded in the adjacent test matrix. Raw diagnostic logs are local/ignored, not audio recordings. No long recording or network upload occurred.

## Documentation research — not call evidence

Microsoft documents process-tree inclusion using `ActivateAudioInterfaceAsync`, `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`, a `VT_BLOB` containing `AUDIOCLIENT_ACTIVATION_PARAMS`, and `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`. It includes descendants and is not restricted to one render endpoint. A target without render streams can yield silence. The sample lists build 20348+. The general activation reference currently says 20438 for the structure; that discrepancy warrants a runtime feature probe, rather than assuming a build number proves support. This Windows 11 build exceeds both. Sources: [ApplicationLoopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/), [activation reference](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync).

The independent microphone path activates `IAudioClient` on an `eCapture` endpoint in shared mode, obtains its mix format, and drains `IAudioCaptureClient` packets. It is not the calling application's processed uplink: app mute/noise suppression may differ from the raw mic stream. Do not assume muting Zoom/Teams stops the companion microphone. Offer a companion Stop/Pause and mute clearly. [Capture sequence](https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream).

`GetBuffer`/`ReleaseBuffer` must be paired on the same thread. Handle `SILENT`, `DATA_DISCONTINUITY`, and `TIMESTAMP_ERROR` explicitly. Packet QPC timestamps are already converted to 100 ns units; they are not raw performance-counter ticks. Source positions and timestamps must carry validity flags. [GetBuffer contract](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer).

Audio-session discovery must preserve non-error success codes: `AUDCLNT_S_NO_SINGLE_PROCESS` reports that a session spans processes and the PID identifies the original creator. It cannot establish exclusive ownership. [GetProcessId contract](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol2-getprocessid). Maintain notification subscriptions plus refreshed enumeration in production; a snapshot alone is not a reliable live session list. [Session enumeration guidance](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionenumerator).

## Zoom/Teams discovery investigation strategy

No renderer names or PIDs are assumed. A live candidate requires observed executable/package identity, process creation time, parent chain and session evidence. The spike uses Toolhelp snapshots, actual session PID/state, and name hints plus observed descendants; it does not yet verify executable signature/path or correlate windows. Those are production requirements.

For each app, separately test meeting and unscheduled direct call. Take read-only process/session snapshots before join, while remote audio is active, on hold/resume, after leave, and after restart. Map each session to the process snapshot and record unknown, inaccessible or multi-process cases. Record actual app versions. Observe whether audio comes from the selected root, descendants, a separate helper or a shared renderer; do not infer this from UI window ownership.

During an operator-controlled call, alternate labeled remote-only speech, local-only speech, overlap, silence, and unrelated local media. Use a headset first, then a speaker configuration for acoustic leakage. Select the narrowest *observed* tree that contains the call renderer, and test it audibly. Broader roots may include app notifications, ring tones, shared content and other calls; process capture does not semantically mean incoming speech only. Choosing an individual helper may miss future renderer children or replacements. If the renderer is outside the tree, show ambiguity and reselect; do not quietly widen to a browser root or system output.

Bind the selected root to PID + creation time + process handle. On exit, stop that stream, emit a gap/source-lost state and rediscover candidates; a reused numeric PID is a different source. The spike holds a process handle and detects exit, then requires a new invocation. Parent IDs from a snapshot are only hints and may themselves have been reused. Production must validate process lifetimes and package/executable identity before any reconnect.

## Recovery and integration recommendation after MC-001 acceptance

Keep a C++20 x64 helper with dedicated COM/capture workers and RAII-managed COM pointers, buffer leases, events and process handles. Port observed API sequences, not this spike's ad hoc JSON or hand-written ABI declarations. Use SDK headers (the first probe run exposed an incorrect copied interface GUID, fixed before successful tests). Freeze integration against Agent A's accepted interfaces before adding IPC.

Recommended behavior, **not implemented recovery claims**:

| Trigger | Response |
|---|---|
| Root process exits/restarts | Source-lost + gap; stop old client; rediscover with fresh identity; never retry the old PID indefinitely |
| Renderer/session changes while root lives | Re-evaluate session/tree evidence; health timeout plus operator probe; require selection if new scope is ambiguous |
| Selected mic unplugged/invalidated | Stop local stream, explicit device-lost; retain remote capture when possible; reacquire same endpoint ID or request new selection |
| Default-device change | Distinguish explicitly pinned endpoint from follow-default policy; show change before adopting another source |
| Output route/Bluetooth profile change | Process loopback is endpoint-independent by design, but verify packet continuity; recreate if invalidated, mark gap; no assumed acoustic/format continuity |
| Permission denied / unsupported format/build | Surface HRESULT and stage; fail that source; no system-loopback substitution |
| No packets / low level | Keep uncertainty separate from API-ready; silence can be legitimate, a wrong tree, mute, or low-level noise |
| Activation timeout/late callback | Abandon generation, retain callback lifetime safely, ignore/release late result; watchdog helper if native call hangs |
| Driver/service failure | Stop affected clients, bounded reconnect/backoff after re-enumeration; error budget then manual recovery |
| Stop/Pause | Cancel capture work, stop clients, drain/release owned buffers, close helper/IPC; do not forward old audio after resume |

Register `IMMNotificationClient` for endpoint/default-device changes and audio-session notifications for session changes; dispatch work out of callbacks and avoid blocking/unregistering in callbacks. [Endpoint notification API](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient), [device events](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events).

Assign a source generation to every capture instance; preserve start/end QPC, timestamp validity, sequence, format, gap reason and dropped-frame counts through accepted IPC. Normalize/downmix each source independently to the agreed 24 kHz PCM16 mono format with clipping checks and resampler-delay accounting. Keep bounded buffers and no network or console logging on capture callbacks. Map clocks against monotonic time, not transcript arrival order. Add external helper heartbeat/watchdog: this experiment observed multi-second blocking/delay. Production UI must be able to show stalled capture even if its native worker is stuck.

The helper only captures. The two STT sessions, client VAD, reasoning and manual screenshots are outside this spike. Mic source does not prove YOU; loudspeaker echo can carry remote speech into it. Headset testing and measured echo/correlation handling are required. App output is mixed REMOTE, never named individuals.

## Browser and system-loopback limitations

WASAPI selects a process tree, not a web tab, meeting URL or participant. Chromium documents shared renderer processes and multiple process types; **inference:** selecting a browser tree can include other tabs/windows, while selecting one renderer need not identify the actual audio path. A dedicated browser profile/window is not an isolation guarantee. Browser calling remains untested. Show `Browser audio — other tabs may be included`, enumerate actual audio sessions, and verify with two simultaneous sources before making a narrower claim. [Chromium architecture](https://www.chromium.org/developers/design-documents/multi-process-architecture/).

Endpoint loopback captures the selected render endpoint's shared mix, potentially including unrelated playback and notifications. It cannot generally recover protected streams and is not an exclusive-mode capture solution. If later implemented, use explicit opt-in, the selected endpoint, a separate fallback source label and `System output (unisolated)` status; do not silently call it Zoom/Teams remote audio. Keep the mic separate, account for acoustic echo, and renegotiate on device loss. This spike intentionally offers no endpoint fallback. [Microsoft loopback limitations](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording).

## Remaining risks and next safest action

Observed synthetic success is encouraging but covers one workstation, one active endpoint pair and a simple renderer. Real app topology, child-process replacement, direct-call transitions, app restart/update, protected/exclusive/offload paths, Bluetooth/USB routing, remote desktop, echo, 100 live start/stop cycles, external handle-leak checks, and production latency remain unverified. Notifications/reconnect and calibrated synchronization are design work, not implemented features here.

Next: use the matrix below in four controlled calls (Zoom meeting/direct; Teams meeting/direct) with an authorized peer and a headset. Attach anonymized snapshots and simultaneous meter evidence, including unrelated-media exclusion and explicit degraded states. Only after those observations and MC-001 contract acceptance should MC-002/003/004 production implementation proceed; do not mark their gates complete from this spike.
