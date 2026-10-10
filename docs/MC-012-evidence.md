# MC-012 implementation and acceptance evidence

Date: 2026-10-10. Branch: `orion/mc012-meeting-coordinator`. Initial base: accepted `Deveadra/cintra` main `e66482adf6e1f21a57da0f1b6183a729e9194ec2`. Updated by merging accepted main `5aecafca6644a58c682945fcc2a2cf578d19d2e2` (MC-009), preserving that implementation and resolving the solution's additive project entries. GitHub issue: #9.

## Done

The desktop now owns a serialized meeting coordinator with explicit app/microphone selection, native capture, separate STT transports, bounded conversation state, one ClockMapping per meeting, per-stream health/levels, typed partial/final transcripts, gaps/source changes, Start/Pause/Resume/Stop and cleanup-aware window closure. The source lease spans startup and capture cleanup; source/PID/renderer uncertainty requires fresh discovery and an explicit operator choice. Pause releases resources, invalidates pending events/voice, and Resume recreates capture/transports on the original meeting timeline. Snapshot remains manual and exposes a borrowed in-memory PNG/metadata/as-of-context handoff for MC-009.

The default capture-only mode is visibly Degraded/unavailable. Offline TEST MODE exercises the accepted VAD/parser with clearly scripted text, no key and no network. The live provider path refuses missing approval/key before starting capture. CI builds the native helper before packaging the desktop. Existing Audio/STT/Platform/Core/Capture implementations and frozen contracts/schemas are unchanged.

## Verified locally

Environment: Windows build 26200 x64; .NET SDK 10.0.401; Windows SDK 10.0.26100.0; Visual Studio 2026 BuildTools; MSVC 19.51.36260.0; CMake/Ninja/CTest in the Visual Studio environment. Initial Git/.NET sandbox restrictions were verified as sandbox access failures; required tools worked outside that restriction. No stack substitution occurred.

| Command | Actual result |
| --- | --- |
| `./scripts/check-environment.ps1` | Windows x64, SDK/runtime and Windows SDK available |
| `./scripts/native-build.ps1` | C++20 x64 Release build; native CTest **2 passed, 0 failed** |
| `./scripts/build.ps1` | Release build **0 warnings, 0 errors** |
| `./scripts/test.ps1` | **222 passed, 0 failed, 0 skipped** across eight test assemblies after updating with newer main; synthetic replay `result: PASS` |
| `dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore` | Passed, exit **0**, after the final build/tests |
| `dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts` followed by `git diff --exit-code -- docs/contracts` | Passed, exit **0**; no frozen-schema drift |
| `dotnet run --project tests/SessionWindows -c Release --no-build --no-restore` without acknowledgments | Refused with `SMOKE_NOT_STARTED`, exit **2**, before capture/provider work |

Full-suite totals after integrating newer main: Unit **39/39**, Contract **27/27**, Audio **11/11**, Platform **31/31**, Capture **18/18**, STT **16/16**, AI **50/50**, new Session **30/30**. TRX files are local ignored artifacts under `TestResults`; CI uploads its own TRX artifacts. The desktop output contains the built `Cintra.Audio.Native.exe` (140800 bytes in this local build).

The Session suite exercises real coordinator side effects through fake inventory, clock, capture and provider seams, plus the actual DualStreamTranscriber/VAD/provider parser, actual NativeAudioCaptureSession named-pipe subprocess boundary with synthetic helper input, and actual WPF controls/event handlers on an STA dispatcher. No hardware microphone, application audio or paid provider was used by those tests.

Meaningful negative paths: missing/replaced mic, unsupported platform, ambiguous shared renderer, inaccessible/recycled root identity, renderer replacement, dead lease, source change, helper/capture failure on either side, one-side socket startup failure, reconnecting STT, stale health while inventory polling is delayed, duplicate aggregate health, out-of-order/stale/wrong-session transcript events, Stop during startup, caller cancellation during and after startup, startup deadline, cleanup deadline, failed graceful shutdown after successful fallback disposal, nonoverlapping probes, late probe failure after restart, and stale snapshot tickets. Stop clears memory; leases are disposed exactly once after cleanup. Test helpers owned by the session testhost are absent after Pause/Stop; another test project's helper is never terminated or counted as owned.

The full-suite run exposed a cross-project fixture-name collision with the accepted Audio test's global helper assertion. It was corrected by compiling the same accepted fake-helper source into `Cintra.Session.FakeHelper.exe` for the Session tests. No existing test was weakened or modified. Startup cancellation and failure classification defects found in earlier regression runs were corrected before the final passing suite.

The first PR CI run also exposed timing sensitivity in the unchanged legacy audio pause/resume fixture: it timestamps resumed frames at zero, so concurrent test-host load can exceed the existing two-second audio freshness bound. The push run passed on the same head while the PR run failed that assertion. The shared test script now uses MSBuild `-m:1` to serialize project-level execution for Windows subprocess/dispatcher fixtures. Assertions, production deadlines and intra-test concurrent pipelines are unchanged; failures are not retried or ignored. Source-loss uncertainty is conservatively marked from the last healthy capture and last confirmed process-tree validation, with explicit interval assertions.

## Not verified and integration gaps

| Environment / scenario | OS / hardware / app version | Evidence in this change | Status |
| --- | --- | --- | --- |
| WPF lifecycle controls and typed display | Windows build 26200; injected capture/provider; no visible native capture | Real STA dispatcher/handlers/control assertions and cleanup on Close | VERIFIED OFFLINE |
| Native adapter process/IPC cleanup | Windows build 26200; synthetic fixture executable | Actual helper subprocess/named-pipe boundary; owned-process cleanup assertions | VERIFIED OFFLINE |
| Interactive desktop with real mic + selected tone process | Operator must record Windows build, anonymized mic/headset, tone PID/creation identity and displayed levels | Runnable procedure and coordinator smoke harness in `MC-012-operator.md`; not executed here | NOT VERIFIED |
| Zoom meeting | OS/hardware/Zoom version must be recorded by operator | No labeled speech test | NOT VERIFIED |
| Zoom unscheduled direct call | OS/hardware/Zoom version must be recorded by operator | No labeled speech test | NOT VERIFIED |
| Teams meeting | OS/hardware/Teams version must be recorded by operator | No labeled speech test | NOT VERIFIED |
| Teams unscheduled direct call | OS/hardware/Teams version must be recorded by operator | No labeled speech test | NOT VERIFIED |
| Physical mic loss, Bluetooth/USB route change, acoustic echo | Operator must record device/mode and source identity | Coordinator failure fixtures only | NOT VERIFIED |
| Manual snapshot/hotkey with real window, region and multi-monitor DPI during live capture | Operator must record display layout/DPI and intended foreground source | Existing capture tests + fake snapshot shared-clock regression | NOT VERIFIED IN INTEGRATED LIVE SHELL |
| Real `gpt-live-transcribe` account/handshake/quality | Separately authorized provider/key/budget | Offline protocol fixtures; paid gate refusal tests | NOT VERIFIED; NO PAID CALL |
| MC-009 vision/AI bridge | Separate issue/service | PNG/metadata/context seam only | NOT IMPLEMENTED HERE BY SCOPE |

## Risks and next safest action

Configured transport-open health measures the configured receive/send lifetime, not recognition accuracy, billing, or the physical identity of speech. Open-but-idle sockets rely on the accepted transport's keepalive/error detection. Zero-level/native silence cannot establish call source correctness. External speakers can leak remote speech into YOU. Manual/browser process trees can include unrelated child-rendered playback and notifications. Snapshot handoff callers must honor its borrowed lifetime and cancellation; no vision calls are implemented.

Run the explicit operator Windows smoke from `MC-012-operator.md` and record anonymized hardware/version/health/cleanup evidence before treating that manual acceptance gate as satisfied. Run any paid rehearsal only after separate approval. Actual Zoom/Teams compatibility remains governed by the existing platform matrix. The Guardian ruleset requires the `windows-offline` check and an up-to-date PR with no bypass actors. Do not bypass that rule or claim manual gates passed from offline/CI results.
