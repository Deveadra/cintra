# Agent B environment and acceptance evidence

Date: 2026-10-09 (America/Chicago). One Windows 11 Home 25H2 x64 workstation, build 26200.9457; active Realtek speaker/microphone endpoints. .NET SDK 10.0.401 / runtime 10.0.12. Zoom executable 7.1.9.48550 and Teams package 26260.1701.5139.3736 installed. Neither app was running in discovery snapshots. Driver version, exact physical microphone/speaker arrangement and headset configuration were not recorded; do not infer them from endpoint names.

## Actual automated/native evidence

Commands below ran from `spikes/windows-audio/`. Build script sets the local CLI-home and SDK environment. `PASS` describes only the stated experiment.

| Command / experiment | Exact result or relevant output | Classification |
|---|---|---|
| `.\build.ps1` | `Build succeeded. 0 Warning(s) 0 Error(s)`; exit 0 | Build verified, C# x64 native interop host |
| `WindowsAudioSpike.exe self-test` | `{"kind":"test_summary","passed":26,"hardware":false}`; exit 0 | Offline checks verified |
| `WindowsAudioSpike.exe inventory`, sandbox | Both Realtek endpoints enumerated; session access `0x80070005 E_ACCESSDENIED` | Explicit partial/failed inventory, not absence of sessions |
| Same inventory outside sandbox | candidate_count 0; render count 1; session count 2 (both inactive); capture count 1; exit 0 | Native read-only inventory verified |
| `WindowsAudioSpike.exe capture --pid self --mic 0 --seconds 3 --acknowledge-capture` | remote 287 packets / 126567 frames; mic 135 packets / 64800 frames; both result 0; process exit 0 | Simultaneous API/packet proof, no voice identity proof |
| `WindowsAudioSpike.exe capture --pid 2147483647 --seconds 1 --acknowledge-capture` | Process not running; zero packets; exit 2 | Invalid source handled; no fallback |
| Manual synthetic selected/sibling captures | Selected peak 0.030059814453125; excluded peak 0.000030517578125; fixture and both captures exit 0 | Initial process isolation observation |
| First `test-native.ps1 -AcknowledgePlayback`, before readiness handshake | FAILED: selected and excluded both 0.000030517578125; selected capture reported 3205 ms packet age; script exit 1 | Retained negative startup/timing evidence |
| Final `.\test-native.ps1 -AcknowledgePlayback`, with readiness handshake | `{"test":"synthetic_process_isolation","includedPeak":0.03009033203125,"excludedPeak":3.0517578125E-05,"result":"PASS","callCompatibility":"NOT_VERIFIED"}` | Reproducible synthetic isolation verified |
| Same final native script, renderer naturally exits | `{"test":"source_exit","result":"PASS"}`; probe expected exit 2 with `source_exited` | Native source-loss handling verified |
| Same final native script, invalid mic and 3 fresh self captures | `{"test":"invalid_mic_and_three_fresh_capture_cycles","result":"PASS","handleLeakProof":false}`; script exit 0 | Bounded stop and repeat invocation verified |
| `dotnet format .\WindowsAudioSpike.csproj whitespace --no-restore` | Sandbox build-host named pipe denied; same project-only formatting outside sandbox exit 0 | Formatter executed |
| `dotnet format .\WindowsAudioSpike.csproj whitespace --no-restore --verify-no-changes` | No output; exit 0 after final formatting | Formatting verified |
| `git status --short --untracked-files=all`, `git diff --check` | Only 13 new files under the two permitted research paths; no tracked-file changes; diff check exit 0 | Scope reviewed; generated binaries/logs ignored |

`WindowsAudioSpike.exe` above means `.\bin\Debug\net10.0-windows\WindowsAudioSpike.exe`. Final branch was `orion/agent-b-windows-audio-spike`; both HEAD and main remained `7365340b66f45468a3a0cedd35a9888367bee7d9`. No spike process was visible in the final process check. No production build/test suite existed in the starting checkout; Agent A's developing shared files were not part of this spike's build or tests.

The 26 offline checks cover ABI sizes; explicit capture authorization/source/duration validation; independent meters; PCM16/24/32 and float32; silent/null buffers; non-finite values; clipping; discontinuity and timestamp validity; unsupported formats; no-packet/low-level/stalled health; activation HRESULT, cancellation, timeout and late completion; and 100 fake failure cycles. They do not simulate a complete device notification/reconnect pipeline. No 100-cycle hardware or leak-proof claim is made.

Early build investigation: `dotnet --info` printed SDK/runtime information but failed querying the Service Control Manager in the sandbox. Initial `dotnet build` failed writing the user's default `.dotnet` path; local `DOTNET_CLI_HOME` fixed it. Its first-run output reported installing an ASP.NET development certificate; trust was not requested or independently checked. The checked-in build script disables certificate generation and global PATH setup for subsequent fresh homes. An initial wrong IMMDeviceCollection IID yielded `E_NOINTERFACE`; corrected against Microsoft's SDK header before endpoint/capture successes. Neither initial failure is evidence that Windows lacks the API.

## Anonymized real-application matrix — ALL NOT VERIFIED

Use the observed OS/app versions as the starting environment only. Refresh versions and devices at the time of each actual call. A blank renderer identity means no call topology was observed, not a guessed parent executable.

| Scenario | App version available | OS/hardware | Renderer/tree/session evidence | Mic vs remote / unrelated media | Recovery | Result |
|---|---|---|---|---|---|---|
| Zoom desktop meeting | 7.1.9.48550 executable | Build 26200.9457; Realtek pair; physical configuration unknown | None; no call/app running | Not exercised | Not exercised | NOT VERIFIED |
| Zoom desktop unscheduled 1:1 | Same | Same | None | Not exercised | Not exercised | NOT VERIFIED |
| Teams desktop meeting | MSTeams 26260.1701.5139.3736 | Same | None; no call/app running | Not exercised | Not exercised | NOT VERIFIED |
| Teams desktop direct call | Same | Same | None | Not exercised | Not exercised | NOT VERIFIED |
| App restart/update, hold/resume, transfer and renderer replacement | Refresh on test | Same or documented second system | No app-specific evidence | Not exercised | Synthetic process exit only | NOT VERIFIED |
| USB headset device removal/reinsert | N/A | No such active endpoint observed | N/A | Not exercised | Not exercised | NOT VERIFIED |
| Bluetooth profile transition | N/A | No such active endpoint observed | N/A | Not exercised | Not exercised | NOT VERIFIED |
| Headset vs speakers / acoustic echo | N/A | Physical setup unknown | N/A | Ambient mic packets only; no labeled voices | N/A | NOT VERIFIED |
| Browser Zoom/Teams with another audible tab | Browser/app versions not collected | Same | No tab/process audio mapping | Not exercised | Not exercised | NOT VERIFIED |
| Endpoint-loopback fallback | N/A | Same | Deliberately not implemented | Unisolated design only | Not exercised | NOT VERIFIED |
| RDP, protected/exclusive/offload render, second hardware configuration | N/A | Not exercised | None | Not exercised | Not exercised | NOT VERIFIED |

## Reproducible manual call protocol

1. Record OS full build, driver version, app executable/package version, selected physical input/output, app input/output settings and headphone/speaker arrangement. Use aliases such as `CALL_ROOT_A`, `RENDERER_A`, `MIC_A` in shared reports. Keep a temporary in-memory alias mapping for live PID selection; do not publish account paths, meeting IDs or participant names.
2. Start with no call. Run inventory. Join a controlled call with an authorized peer manually; run inventory again during remote playback. Capture observed parent/child/session relations and whether any session spans processes. No hardcoded renderer name is an acceptable substitute.
3. For each 10–20 second bounded run, announce the source and perform labeled remote-only speech, local-only speech, silence, then overlap. Verify both meters and actual audible source at the workstation. A meter alone cannot distinguish speech from a notification.
4. Play unrelated media in a separate local process during remote playback, then while remote is quiet. Verify exclusion for the selected tree and document any leakage or ambiguous shared scope. Keep app notifications/content sharing distinct from remote speech.
5. Repeat with the app minimized, then hold/resume and leave/rejoin; Teams transfer and Zoom waiting-room transition where available. Take new process/session snapshots rather than assuming renderer stability.
6. Test app restart and device switches manually. Record source-loss time, gap interval, rediscovery, operator selection and first valid restored packet. This spike stops on loss and requires re-invocation; do not call that automatic reconnect.
7. Verify Stop/Ctrl+C and duration expiry release audio. Production gate additionally requires 100 start/stop cycles and external thread/handle/device-access inspection; fresh-process spike runs alone are insufficient.
8. Complete all four call modes plus a second device configuration. Record failures as well as successful runs. No default recording; if an audible replay is necessary, separately authorize a short synthetic/consented fixture. Publish no participant audio/transcript.

## Gate decision

**Done:** isolated prototype, source-discovery tooling, synthetic native experiment, documentation, reproducible harness and manual matrix.

**Verified:** build, offline tests, native endpoint/session inventory (permissions caveat), independent mic/process packets, selected synthetic renderer inclusion, unrelated synthetic renderer exclusion, invalid sources, natural process exit and three fresh captures.

**Not verified:** Zoom/Teams compatibility, actual call renderer topology, physical voice separation, full reconnect/device changes, real Ctrl+C key injection, hard cancellation deadline, 100-cycle native soak/leak checks, STT/IPC/resampling.

**Risks:** scheduling/native-call stalls, source ambiguity, one-LSB background samples, microphone echo, browser sharing, PID reuse and untested driver/device combinations.

**Next safest action:** four controlled call tests using a headset and these scripts, then map observations into MC-001's accepted contracts before implementing production audio tasks.
