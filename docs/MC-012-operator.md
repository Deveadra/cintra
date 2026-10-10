# MC-012 meeting shell

Issue: https://github.com/Deveadra/cintra/issues/9. Implemented from accepted main `e66482adf6e1f21a57da0f1b6183a729e9194ec2` on `orion/mc012-meeting-coordinator`.

## Build and start

Required: Windows 11 x64, .NET SDK pinned by `global.json`, Visual Studio MSVC x64 tools, CMake/Ninja/CTest supplied by Visual Studio, Windows SDK, Git. Stop if any required tool is absent. Do not change stacks. The accepted build scripts fail rather than substitute a compiler.

From the repository in PowerShell:

```powershell
./scripts/check-environment.ps1
./scripts/native-build.ps1
./scripts/build.ps1
./scripts/test.ps1
dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore
dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts
git diff --exit-code -- docs/contracts
dotnet run --project src/MeetingCompanion.Desktop -c Release --no-build --no-restore
```

Build native before managed to copy `Cintra.Audio.Native.exe` beside the desktop executable. CI now uses that order. Repository development also resolves `artifacts/native/Cintra.Audio.Native.exe`. A missing helper fails Start with an actionable error. Snapshot native DLL build remains owned by the accepted Capture project.

1. Choose Zoom, Microsoft Teams, or Manual process selection. **Refresh audio sources** performs read-only inventory. Choose the exact incoming process tree and microphone endpoint. A source is never automatically selected by numeric PID. Refresh requires stopping the previous meeting first.
2. Choose the provider. The default **Capture only** opens native capture without a key or network socket and remains **Degraded — Transcription unavailable**. **TEST MODE** uses real native capture and local VAD with offline scripted provider replies. Its text is canned, never recognized speech. It reads no key and never opens a network connection.
3. **Start** revalidates the microphone inventory and root/renderer identities, acquires a process lease, checks it immediately before native startup, then starts fresh capture/transcription resources. Both streams must show fresh Healthy/Silence capture and configured STT transport before **Listening**. Zero-level sources stay Degraded, including idle or possibly wrong renderers. Render activity alone is not audible call evidence.
4. **Pause** marks both voice streams interrupted and releases native capture and both transports. **Resume** revalidates the held source and starts new capture, VAD, and provider sessions on the same meeting clock/context. It never replays uncertain audio. Resume may produce new partial/final items after its fresh turn boundary.
5. A dead/recycled PID, changed renderer/tree, changed source, microphone/capture failure, or helper crash cancels voice processing, marks uncertainty, clears meeting data and releases resources. Refresh and explicitly choose again. A network/transcription failure is displayed per side; it cannot leave false Listening.
6. **Stop** cancels startup before waiting for the serialized owner, invalidates late events, clears transcripts/context/images, and waits for cleanup. Cleanup timeout leaves an Error and preserves the lease until cleanup completes. Stop again to finish; restart and successful closure are blocked while cleanup remains uncertain. A failed graceful Stop remains visible even if fallback disposal completed. Window close follows the same cleanup path and stays open on a cleanup error.

The always-on-top panel supports keyboard tab navigation and Alt access keys for lifecycle controls. Per-stream meters display native peaks; stale capture health resets its meter and degrades the session. YOU means microphone source and may contain speaker echo. REMOTE means the selected application's mixed render audio, including notifications; it never implies remote speaker names. Browser trees may contain other tabs.

Snapshot remains a manual button or Ctrl+Shift+configured-letter operation, with the accepted window/region/display picker and preview. No vision or screenshot upload is added. The snapshot uses the meeting's exact ClockMapping and supplies PNG + metadata + as-of finalized context through `MeetingCoordinator.LatestSnapshot`. The handoff is borrowed in-memory data; consumers must not retain it after ClearSnapshot/Stop. A generation ticket rejects captures completed after Stop, Pause, Resume, failure or restart. Preview and handoff PNG buffers are cleared when discarded. There is no automatic capture.

Only call-app type, microphone endpoint ID and a descriptive last-choice label persist in `%LOCALAPPDATA%\Cintra\source-preferences.json`. Stored PIDs are descriptive labels, never identities to follow. No audio, transcript, screenshot or credentials are persisted. Last-choice settings never replace fresh validation and explicit app selection. Unwritable settings fall back to in-memory settings.

## Offline-provider Windows smoke with real native capture

Requires an operator-selected microphone and explicit capture/playback acknowledgments. This is **not run by CI**. The operator must hear the synthetic source and verify the PID carries it.

```powershell
dotnet run --project tests/PlatformWindows -c Release --no-build --no-restore
```

The platform inventory intentionally prints anonymized endpoint labels rather than IDs. Obtain the fresh ID using `./artifacts/native/Cintra.Audio.Native.exe --inventory`, or select the endpoint directly in the desktop picker. Launch `./artifacts/native/Cintra.Audio.Tone.exe --acknowledge-playback --seconds 30` in a second terminal and use its printed `tone_ready pid=...`; select that PID through Manual in the desktop. Check both meters, canned turn labels, Snapshot, Pause/Resume and Stop. An uninterrupted tone may keep a VAD turn open until the maximum turn duration; use alternating speech/silence or wait for that boundary to observe a scripted final. The fixture naturally exits after thirty seconds; allow time for inventory/startup and handle expiry by selecting a fresh source.

The coordinator smoke harness performs Start, reports health for four seconds, Pause, Resume on the same meeting timeline, reports four more seconds, and Stop:

```powershell
dotnet run --project tests/SessionWindows -c Release --no-build --no-restore -- --mic '<fresh endpoint ID>' --pid <selected synthetic PID> --acknowledge-capture --acknowledge-playback
```

Without both acknowledgments and valid mic/PID arguments it exits 2 with `SMOKE_NOT_STARTED` before inventory, hardware capture, or any provider work. Successful exit reports resource release and memory clearing, not Zoom/Teams compatibility. Use the desktop to inspect manual screenshots/hotkeys and source changes. End the synthetic fixture using its documented lifecycle; the companion owns only its native helper, not the selected application.

## Separately gated real OpenAI provider test

No paid API test was performed for this issue. Credentials are never printed, provisioned or logged by the shell. The desktop reads an existing process `OPENAI_API_KEY` only when **OpenAI** is selected and the operator checks **I approve paid API use for this session** and presses Start. Missing approval/key fails before native capture or socket creation. Configure credentials through the secure OpenAI Platform workflow; do not paste them into the repository, console output or issue comments.

The accepted short paid rehearsal remains separately gated by `--approve-paid-api`, capture/playback acknowledgments, 8–20 seconds and a maximum-cost argument. Consult `docs/stt.md` for MC-006 rehearsal instructions before running it. The shell reuses the accepted `gpt-live-transcribe`, PCM24k, client-VAD transcription adapter; this change makes no new live capability claim. Provider failure does not turn on vision/reasoning or upload snapshots.

## Integration boundaries

MC-009 consumes the narrow snapshot handoff later; no vision/reasoning integration is implemented here. Actual Zoom/Teams labeled-speech meeting/direct-call acceptance, physical device disconnect/reconnect, speaker echo, multi-monitor hotkey behavior and live provider quality remain separately unverified. TEST MODE and subprocess fixtures do not establish any of those claims. Release gates remain in the existing platform matrix and the MC-012 evidence file.
