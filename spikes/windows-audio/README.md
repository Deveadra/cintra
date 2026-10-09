# Standalone Windows audio feasibility spike

Agent B preparation for MC-002/003/004. This project is independent of MC-001 and must not be referenced by production projects. It uses .NET 10 x64 as a native WASAPI/COM test host because no C++ compiler/Windows SDK was found on the investigation workstation. No NuGet packages, cloud APIs, credentials, audio files, transcripts, or production interfaces are involved. Production C++ implementation is still recommended.

## Build and offline checks

From PowerShell in `spikes/windows-audio`:

```powershell
.\build.ps1
.\bin\Debug\net10.0-windows\WindowsAudioSpike.exe self-test
```

`build.ps1` keeps .NET CLI state under the ignored spike directory, disables telemetry, and restores against an empty package source list. Requires an installed .NET 10 SDK. Tests use synthetic memory buffers and fake activation completions; they do not touch microphones, speakers, calling apps, or provider APIs.

To format/check just this project (after build.ps1 has set the environment):

```powershell
dotnet format .\WindowsAudioSpike.csproj whitespace --no-restore
dotnet format .\WindowsAudioSpike.csproj whitespace --no-restore --verify-no-changes
```

## Read-only inventory

```powershell
.\bin\Debug\net10.0-windows\WindowsAudioSpike.exe inventory
```

Emits JSON lines for active endpoints, active/inactive audio sessions, observed PID/parent relationships, and name-based Zoom/Teams candidates plus descendants. Session state values: 0 inactive, 1 active, 2 expired. `multiProcess=true` means the returned PID is not the exclusive owner. PID 0/system-sounds is never an application selection. Inventory is a point-in-time snapshot; it does not subscribe to new sessions. Process names are hints, not proof of call media. No active candidates does not mean an application is not installed.

Sandboxed execution on the research workstation allowed endpoint enumeration but denied session enumeration (`0x80070005`); ordinary execution outside that sandbox succeeded. This does not establish a requirement for administrator privileges. Run under the interactive user's normal token first. The probe reports permissions failures instead of translating them into an empty successful result.

## Explicit bounded capture

Re-run inventory, choose the microphone's current active index and an actually observed process ID. Do not copy stale PIDs from a report.

```powershell
# Independent mic and selected process tree, maximum requested duration 30 seconds.
$targetProcessId = Read-Host 'Observed call audio process PID'
.\bin\Debug\net10.0-windows\WindowsAudioSpike.exe capture --pid $targetProcessId --mic 0 --seconds 10 --acknowledge-capture

# Safe process-loopback baseline: only the probe's own silent process tree.
.\bin\Debug\net10.0-windows\WindowsAudioSpike.exe capture --pid self --seconds 3 --acknowledge-capture
```

Every source is printed before capture. Ctrl+C and the shared duration token request stop. Each source has its own worker, WASAPI client, packet counters, format, meter and status. One source failing does not silently switch or relabel the other. Any source error produces exit 2; exit 0 means the run ended normally, **not** that speech or application identity was verified.

Duration bounds are cooperative: synchronous Windows calls can delay cancellation. The native test script uses a separate 12-second child-process watchdog. A production helper needs its own external watchdog; this spike is not a hard real-time guarantee. Activation completion has a 5-second timeout. Late callbacks release their result without restarting capture. Never free a pending callback while Windows can still call it.

Capture reports peak/RMS, packet/frame counts, clipping, discontinuity flags, and valid last-packet QPC/device timestamps. `GetBuffer` QPC values are already in 100 ns units. No sample arrays are retained after `ReleaseBuffer`. The initial UTC/QPC pair is diagnostic, not a calibrated cross-device clock mapping. The loopback device position observed here remained zero; do not use it as the cross-stream ordering clock.

Mic capture uses the endpoint mix format. Process loopback requests stereo PCM16/44.1 kHz with Windows conversion. This does **not** implement production 24 kHz mono normalization. PCM16/24/32 and float32 levels are supported; other formats fail explicitly. One-bit dither is classified as low level using a diagnostic -80 dBFS floor. This floor is not VAD or proof that a source is silent. Meter health never certifies speaker identity. Packet age >=500 ms is a stalled state; synchronous native calls may delay its publication.

## Native synthetic checks (audible, opt-in)

```powershell
.\test-native.ps1 -AcknowledgePlayback
# Optional additional 3-second microphone test, after confirming index 0:
.\test-native.ps1 -AcknowledgePlayback -IncludeMicrophone
```

Plays a low-amplitude 440 Hz tone on the default console render endpoint, using only this project's fixture process. Waits for its first submitted buffer, captures the selected fixture and then the separate self-process while playback remains active, tests source exit, rejects an unavailable mic, and starts/stops three fresh process captures. It writes **only JSON diagnostic metadata and errors** under ignored `bin/native-test-logs/`. It never kills a calling application: the watchdog can terminate only child fixture/probe processes it created.

Isolation assertion: selected peak >=0.005 and excluded peak <=0.0001, with tone process alive through the exclusion interval. Failure is not suppressed or retried into a pass. These criteria are a narrow fixture check, not spectral proof, hearing verification, speech accuracy, or a Zoom/Teams compatibility test. Review `tone.jsonl` and capture logs together. Device volume/effects and scheduling can invalidate a run.

Direct tone invocation is also explicit and limited to 1–10 seconds of requested playback:

```powershell
.\bin\Debug\net10.0-windows\WindowsAudioSpike.exe tone --seconds 3 --acknowledge-playback
```

## Deliberate omissions

No endpoint-loopback fallback, exclusion-of-tree mode, automatic source replacement, persistent endpoint selection, notification callbacks, reconnect implementation, AEC, resampling pipeline, IPC, UI, STT, or command execution. Microphone indices can reorder; production must use endpoint IDs and show changes. Real call acceptance and native leak/soak tests remain separate gates. See `docs/research/windows-audio-findings.md` and `windows-audio-test-matrix.md`.
