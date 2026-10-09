# MC-003–005 native audio handoff — 2026-10-09

## Done

Implemented native C++20 WASAPI microphone and selected-process-tree loopback capture, normalization, timestamp/gap accounting, device notifications, bounded recovery/buffers, secure v1 IPC and the .NET `IAudioCaptureSession` adapter. Added native tests, offline helper integration fixtures, a real hardware harness and native CI build/tests. No meeting/UI/STT/AI behavior was changed. No audio files or credentials were used.

Branch: `orion/native-windows-audio`, based on accepted `main` `274a838214f02dae8c0c131fb2c4aaee41a88e2b`. The requested `Deveadra/cintra` and the checkout's legacy `Deveadra/meeting-companion` remote resolved to that same main SHA before implementation. Final commit/PR references are provided with the handoff and in GitHub, rather than embedding a self-referential commit SHA here. No merge, reset, rebase, cherry-pick or history rewrite was performed.

## Verified

Environment: Windows 11 `10.0.26200.0`, x64; MSVC `19.51.36260` (toolset directory `14.51.36231`), Visual Studio 18 Build Tools, SDK `10.0.26100.0`, CMake `4.3.1-msvc1`, .NET SDK `10.0.401`. Brief native C++20 compile/link and `IMMDeviceEnumerator` COM activation returned `NATIVE_WASAPI_COM_OK HRESULT=0x00000000`.

| Exact command | Actual final result |
| --- | --- |
| `./scripts/native-build.ps1` | x64 Release native helper, tone fixture and two test executables compiled/linked under `/W4 /WX`; CTest **100% tests passed, 0 tests failed out of 2** |
| `dotnet build MeetingCompanion.slnx -c Release --no-restore` | All 14 managed projects: **Build succeeded. 0 Warning(s), 0 Error(s)** |
| `./scripts/test.ps1` | Unit **23 passed**, Contract **27 passed**, Audio integration **11 passed**; zero failures/skips; replay **PASS**, two separately labeled synthetic frames |
| `dotnet format MeetingCompanion.slnx --no-restore` | Exit 0; managed formatting applied |
| `dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore` | Exit 0 |
| `dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts` | Exit 0; frozen schema exports match accepted main after LF normalization |
| `git diff --exit-code -- docs/contracts src/MeetingCompanion.Contracts src/MeetingCompanion.Core` | Exit 0; accepted structural contracts and Core unchanged |
| `git diff --check` | Exit 0 |
| `./artifacts/native/Cintra.Audio.Native.exe --inventory` | Exit 0; one active capture endpoint enumerated |
| `./scripts/native-test.ps1 -AcknowledgeCapture -AcknowledgePlayback -MicrophoneEndpointId '<fresh endpoint A ID>' -Cycles 100` | Exit 0 on final binaries: **PASS real native dual-stream capture; selected/excluded renderer; Pause/Resume; source termination gap; 100 start/stop cycles; no capture helper remains** |

The scripts invoke the verified native developer environment, Ninja, `CMAKE_BUILD_TYPE=Release`, `CMAKE_TRY_COMPILE_CONFIGURATION=Release`, and embedded debug information. Native style was reviewed directly; this repository has no configured native formatter. The existing managed formatter ran successfully.

The restrictive execution sandbox initially caused CMake's Debug compiler probe to report `C1902` and the managed build to exit without useful diagnostics. Approved ordinary-user execution and a Release CMake probe succeeded. No toolchain reinstall or technology substitution occurred. The first native PCM24 test had an incorrect stereo fixture allocation/expected amplitude; the test was corrected before the final passes. The first real harness used the fixture's parent process for exclusion and correctly included its descendant renderer; the harness now selects its own separate process tree. A one-second run initially ended before mic readiness; timing now begins after both sources initialize. A fast fake helper caught a Resume response race; the adapter now enables acceptance before issuing Resume, after successful Pause acknowledgement. These failures were not presented as green compatibility results.

### Anonymized real platform matrix

Same workstation throughout: Windows build `26200`, x64, capture endpoint **A**, default console render endpoint **B**. Endpoints are anonymized; model/firmware and acoustic configuration were not independently characterized. Fixture application is the included native `Cintra.Audio.Tone.exe`, Release build; no Zoom/Teams application version or call was used. This proves one native topology, not calling-app compatibility.

| Test | Observed final evidence | Result |
| --- | --- | --- |
| Selected renderer, two independent streams, including Pause/Resume | Remote **199** 20 ms frames; mic **77** frames; remote peak **0.031585693359375**; mic peak **0.00909423828125**; distinct stream IDs validated by MC-001 deserializer | PASS |
| Unrelated renderer remains playing; capture selects harness's own process tree | Remote **204** frames; mic **150**; remote peak **0.000030517578125**, below exclusion threshold **0.0001** | PASS |
| 100 fresh dual-stream start/stop cycles | Mic frames/cycle **48–51**; incoming frames/cycle **79–197**, including microphone startup interval; every run produced both identities and normal cleanup; no capture helper remained | PASS |
| Renderer root exits naturally during active capture | `source_terminated:0x8007042b`, `source_changed` gap, failed/stopped incoming health; mic continued (**200** frames); expected harness result **FAIL**, exit 2 | PASS fault detection |
| Well-formed absent microphone endpoint | `selected_endpoint:0x80070490`, **three** `no_device` observations, `recovery_budget_exhausted` at about **1.1 s**; incoming continued; expected exit 2; Stop cleaned helper | PASS bounded recovery/failure |
| Malformed endpoint ID | `selected_endpoint:0x80070057`, `nonrecoverable_source_error`; no pointless retry; expected exit 2 | PASS input failure |

Ignored raw metadata is in `artifacts/native-evidence/`, including selected/excluded renderer, 100 lifecycle runs, source termination, malformed and absent-device logs. These contain health, gaps, counts and peaks, never PCM/audio recordings. Endpoint IDs and process IDs are deliberately omitted from this tracked report. To reproduce absent-device recovery, invoke the built harness with `--mic '{0.0.1.00000000}.{00000000-0000-0000-0000-000000000001}' --pid self --seconds 1 --acknowledge-capture`; expected exit is 2 with the errors above. This is a negative test, not a success capture.

### Automated failure coverage

Native tests cover source formats/rates, channel scaling, silent/null input, float NaN handling, PCM24 sign extension, streaming packet partitioning, anti-alias filtering, independent bounded queue overflow/clear, invalid format, recovery budget, selected-device vs unrelated/default notification routing, output-route notifications, late activation after cancellation/error, and 100 callback event-handle cleanup cycles.

Managed audio tests cover separate identities, per-source queue gaps, cancellation/clear, authenticated helper start/pause/resume/stop, repeated helper sessions, absent executable, cancelled startup, helper crash, transport stall, wrong session, one worker stalled while the other keeps heartbeats, Stop timeout/owned-helper termination, and cancelled Stop cleanup. Standard CI uses these fakes and native tests; real capture/playback is explicitly separate.

## Not verified / risks

- Zoom and Teams meetings and unscheduled direct calls; no compatible-app claim is made.
- Physical mic unplug/replug, USB/Bluetooth profiles, actual output-route changes, audio-service recovery and a second workstation/device configuration. Notifications/retry policy were tested offline and absent-endpoint recovery was tested against WASAPI.
- Operator/peer speech accuracy, acoustic echo, AEC, named remote speakers, browser tabs, protected/exclusive/offload content. Mic is raw input; incoming is a mixed process tree.
- Independent clock/physical latency calibration, long-duration drift or publication of a p95 end-to-end latency benchmark. Packet clocks are validated/reanchored and gaps are explicit; they do not establish a physical latency guarantee.
- External device-driver handles were not inspected individually. Helper processes ended after each run; native callback tests independently checked event-handle counts. OS synchronous calls may require helper termination; that is reported as a cleanup error.
- The desktop shell, automatic app discovery/reselection, STT and reasoning integration remain with their owners. Use the accepted selection/session interface and the included harness.
- GitHub-hosted CI and human review are separate release gates; local test output alone does not authorize merge.

## Files changed

```text
.github/workflows/ci.yml
MeetingCompanion.slnx
docs/native-audio-implementation-note.md
docs/native-audio.md
docs/native-audio-evidence.md
scripts/native-build.ps1
scripts/native-test.ps1
src/MeetingCompanion.Audio/MeetingCompanion.Audio.csproj
src/MeetingCompanion.Audio/CaptureInbox.cs
src/MeetingCompanion.Audio/NativeAudioCaptureSession.cs
src/MeetingCompanion.Native/CMakeLists.txt
src/MeetingCompanion.Native/audio.h
src/MeetingCompanion.Native/capture.h
src/MeetingCompanion.Native/capture.cpp
src/MeetingCompanion.Native/main.cpp
src/MeetingCompanion.Native/wasapi_support.h
tests/AudioFakeHelper/Cintra.Audio.FakeHelper.csproj
tests/AudioFakeHelper/Program.cs
tests/AudioHarness/Cintra.Audio.Harness.csproj
tests/AudioHarness/Program.cs
tests/AudioIntegration/MeetingCompanion.Audio.Tests.csproj
tests/AudioIntegration/AudioTests.cs
tests/Native/audio_tests.cpp
tests/Native/lifetime_tests.cpp
tests/Native/tone.cpp
```

## Next safest action

Review the feature PR and passing CI, then perform the operator-assisted Zoom/Teams meeting/direct-call matrix in `native-audio.md`, including unrelated media, labeled speech, source/device loss and a second audio configuration. Attach anonymized OS/app-version/endpoint/process-tree evidence. Keep the PR in draft until the required platform evidence and review are approved; do not merge automatically.
