# Native Windows capture — MC-003–005

This subsystem implements capture, conversion, bounded buffering and the frozen MC-001 native/managed interface. It has no transcription, reasoning, UI automation, meeting integration, recording or network upload. Release readiness still requires the manual call matrix below.

## Interface and deployment

Build `Cintra.Audio.Native.exe` with `scripts/native-build.ps1`; deploy this x64 executable beside the application or provide its absolute path to `NativeAudioCaptureSession`. The caller supplies a validated `CaptureSelection` and `ClockMapping(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow)`. Call `StartAsync`, consume `ReadAllAsync`, and dispose the session. Sessions are single-use after Stop/failure; make a fresh selection and session for a replacement source. The desktop shell is intentionally not wired by this change.

The host implements `IAudioCaptureSession`. It creates a randomly named, first-instance Windows pipe with a protected current-user SID ACL, `PIPE_REJECT_REMOTE_CLIENTS`, and overlapped I/O. Each side verifies the other side's process ID against its explicitly launched helper/host. The helper opens the pipe with identification-only impersonation security. The protocol remains schema version 1: four-byte little-endian positive UTF-8 envelope length, at most 1 MiB, and base64 PCM. Host writes are serialized; one reader owns each side. Invalid sessions, commands, versions, framing, deadlines and EOF abandon the connection. No TCP listener or silent downgrade exists.

Dependencies added: Windows SDK C++/WinRT headers and the Windows OS JSON runtime (`Windows.Data.Json`, linked through `windowsapp.lib`), standard Windows COM/security libraries, and CMake/Ninja supplied by Visual Studio. No third-party native library or new NuGet package is required. The Audio project now targets `net10.0-windows` because its concrete implementation uses Windows pipe ACLs and process APIs; Contracts and Core remain platform-neutral.

## Capture and clocks

The microphone uses the explicitly pinned `eCapture` endpoint ID and its shared-mode mix format. It is independent of the calling app's microphone mute, processing and uplink. Incoming capture uses `ActivateAudioInterfaceAsync` with process-tree inclusion and holds the selected process handle for its lifetime. Exit invalidates that selection; a numeric PID is never reacquired automatically. It captures all audio rendered in that tree, including notifications/shared content; it cannot identify individual participants or browser tabs. No endpoint fallback is implemented.

Each source has its own MTA worker, audio client, capture service, format converter, sequence and notification subscription. Events wake packet draining; polling is bounded at 20 ms. Packet buffer leases are released on the acquiring thread even on conversion/error unwinding. Start/Stop, cancellation, events, notification registration, COM pointers, endpoint format memory and process handles have scoped owners. OS calls can still block; the managed watchdog terminates only its own helper if graceful cleanup or heartbeats fail.

Supported source formats are signed PCM16/24/32, left-aligned extensible valid bits, and float32, 8–192 kHz, 1–32 channels. Unsupported/compressed formats fail explicitly. Channels are averaged; a streaming 32-tap windowed-sinc filter resamples to 24 kHz before clipping-safe signed PCM16 quantization. Filter history and rational phase persist across packets. Lookahead is 16 input frames; timestamp assignment compensates that delay. Packet partitioning tests verify identical output. No channel from one source enters the other's converter.

`GetBuffer` QPC timestamps are converted **100 ns values**, while the clock mapping origin is raw QPC ticks. The helper converts each to the shared session timeline and emits 480-sample/20 ms frames. It does not order by receipt time or device position. Invalid, future or >2 s old timestamps cause gaps and discarded packets. Discontinuities and packet-clock drift >20 ms reset conversion with a marked uncertain interval. Recovery and Pause/Resume retain per-stream sequence monotonicity. Filter tails and partial frames are discarded rather than replayed after a gap. Sub-millisecond clock rounding, unmeasured physical device latency and hardware clock drift remain limitations.

## Failure and retention policy

Both native and managed queues cap audio at 48,000 normalized samples (two seconds) **per stream**. Overflow drops oldest audio from that same stream and reports an interval and dropped sample count. Metadata is separately bounded at 512 messages; exhaustion aborts the transport. Queued audio older than two seconds is discarded. Health reports levels, drop counts and native oldest queued age. No audio callback waits on IPC, network or console logging.

Pinned microphone device changes and process-output topology changes trigger explicit gaps and reinitialization. Recovery allows at most three attempts, separated by 250 ms and 500 ms; unsupported format, permission denial and terminated process are terminal. No alternate microphone, process or system-output source is silently adopted. Repeated failures require a fresh session/operator selection. Device notifications are dispatched by setting atomic flags, with no blocking work in callbacks. Session discovery/reselection remains the Platform adapter's responsibility.

Activation has a five-second deadline; an OS-held callback reference safely owns late results after cancellation/timeout. Stop and Pause prevent managed frame delivery immediately and wait up to three seconds for both source cleanup acknowledgements. Stop additionally waits for helper exit, clears queues and releases the pipe. Failure to acknowledge/exit is an error, followed by termination of the owned helper. The reader enforces two-second transport and per-worker freshness; a starting worker gets an eight-second initialization budget. Pause keeps metadata heartbeats alive. A hung worker forces whole-helper cleanup rather than falsely maintaining a healthy two-source session.

Silence and low signal are not evidence of the correct renderer. The meter uses a 0.0001 peak threshold to avoid treating PCM dither as audible verification; it is not speech VAD or speaker attribution. A physically unplugged microphone, Bluetooth profile change or driver stall still needs real hardware validation. Acoustic echo may carry remote sound into the raw mic; no AEC or named-speaker claim is provided.

Nothing writes audio to disk. The harness prints source selection, health, timing, gaps, frame counts and peaks; its ignored logs contain metadata only. Stop clears retained audio. API tokens are unused.

## Reproduce

```powershell
./scripts/native-build.ps1
dotnet build MeetingCompanion.slnx -c Release
./scripts/test.ps1
./artifacts/native/Cintra.Audio.Native.exe --inventory
```

Use the freshly enumerated microphone endpoint ID; do not copy a stale report ID.

```powershell
./scripts/native-test.ps1 -AcknowledgeCapture -AcknowledgePlayback -MicrophoneEndpointId '<selected endpoint ID>' -Cycles 100
# A selected live audio-producing process, without any audio files:
./tests/AudioHarness/bin/Release/net10.0-windows/Cintra.Audio.Harness.exe `
  '<absolute native helper path>' --capture --mic '<endpoint ID>' `
  --pid <observed renderer PID> --seconds 10 --acknowledge-capture --exercise-controls
```

The script starts only its own low-amplitude 440 Hz renderer, waits for readiness, verifies selected inclusion (peak >=0.005) and unrelated-process exclusion (peak <=0.0001) while the renderer lives, exercises Pause/Resume, runs bounded dual-stream start/stop cycles and checks a naturally exiting renderer produces source-loss diagnostics/gaps. Measurement begins after both sources initialize; initialization has an eight-second bound. A subprocess watchdog bounds each run to 12 seconds. Harness execution may return an error even if some audio arrived: inspect health/gaps and the final result. Never interpret `CAPTURE_ENDED` as speech/application compatibility proof.

## Manual release matrix — all NOT VERIFIED

Record anonymized endpoint labels, headset/speaker mode, Windows build, actual Zoom/Teams versions, selected root/renderer tree and creation identity, timestamped health/levels, gaps and cleanup outcomes. Use an authorized peer and the same labeled speech sequence in each row.

| App/scenario | Required operator participation | Status |
| --- | --- | --- |
| Zoom desktop meeting | Remote-only/local-only speech, overlap, silence; unrelated music; verify selected tree | NOT VERIFIED |
| Zoom unscheduled 1:1 call | Repeat above; hold/resume, leave/rejoin and renderer replacement | NOT VERIFIED |
| Teams desktop meeting | Repeat labeled speech/isolation; screen-share audio and notifications | NOT VERIFIED |
| Teams unscheduled 1:1 call | Repeat; call handoff, hold/resume and restart | NOT VERIFIED |
| Each app with headset, then speakers | Measure acoustic leakage; do not infer YOU from mic alone | NOT VERIFIED |
| USB/Bluetooth loss and reconnect | Unplug/replug, change output route/profile; show gap and bounded recovery without source substitution | NOT VERIFIED |
| App root exit/restart during call | Old process stops; fresh selection required; no PID-reuse capture | NOT VERIFIED |
| Protected/exclusive/offload and browser calling | Explicit errors/isolation limits; no fallback promise | NOT VERIFIED |

Reference API semantics: [Microsoft process loopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/), [WASAPI packet ownership/timestamps](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer), [asynchronous activation](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync). These references explain the implementation; they are not compatibility evidence.
