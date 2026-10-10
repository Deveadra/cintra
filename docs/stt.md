# MC-006 dual-stream live transcription

## Boundary and behavior

`DualStreamTranscriber` owns an accepted `IAudioCaptureSession`, starts it with a validated microphone and isolated `RemoteApp` selection, and consumes `ReadAllAsync`. It routes each 24 kHz mono PCM16 `AudioFrame` by its original `AudioStreamId` and capture timestamps to a distinct `RealtimeTranscriptionSession`. Each session has its own socket, local VAD, bounded audio queue, pending turn map, reconnect state and transport identifier. The common `SessionId` on emitted `ConversationEvent` values is the capture/conversation session ID. No screenshot, reasoning request, app command or audio output passes through STT.

The provider configuration is a transcription session with `gpt-live-transcribe`, `audio/pcm` at 24 kHz and `turn_detection: null`. Local energy VAD defaults to a 0.012 RMS threshold, 40 ms speech start, 400 ms end silence, 120 ms pre-roll and 15 s maximum turn. Thresholds are configurable per deployment. It sends audio only from a detected turn, followed by one `input_audio_buffer.commit` when that turn ends; silence does not commit. An explicit `CommitTurnAsync` can end an active turn but rejects an empty/unrelated turn. These are starting values, not measured speech-quality settings.

`input_audio_buffer.committed` maps provider `item_id` to the oldest locally committed turn on that same socket. Early deltas are held briefly until mapping is known; completed text is authoritative. Duplicate event IDs and late deltas cannot replace a final. A 128-item bound applies to unmatched and pending provider items, and a 512-entry bound applies to recent duplicate IDs and finalized item IDs. Provider completion order does not determine capture order. The v1 typed transcript contract validates every emitted partial/final. The `YOU` label means microphone source and carries an echo caveat; `REMOTE` is the mixed selected process output, never a named participant.

Each audio queue is capped at 2 s. Overflow drops oldest queued PCM, clears the uncommitted provider buffer and emits an `audio_gap` with a dropped-sample count. Capture gaps and source changes also abandon only uncommitted speech. A lost/expired connection discards uncertain queued audio and unresolved provider turns, emits a gap, then reconnects with bounded exponential backoff and jitter. No audio is replayed. Invalid credentials and exhausted retries are terminal. Provider rate limiting is surfaced and retried within the same bound. Only an aggregate health event with both capture components and both STT components healthy/silent may claim `Listening`; isolated component health cannot.

Audio, provisional text and provider mappings live in memory only. Stop and Dispose clear queues and sockets. No recording, transcript or key is logged or retained by default. `.env.local` is ignored by Git.

## Offline verification

From the repository root on Windows:

```powershell
./scripts/check-environment.ps1
./scripts/native-build.ps1
dotnet restore MeetingCompanion.slnx
dotnet build MeetingCompanion.slnx -c Release --no-restore
./scripts/test.ps1
dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore
dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts
git diff --exit-code -- docs/contracts
```

The focused fake provider suite covers two simultaneous streams, VAD silence/short pause, one commit, fragmented and early events, out-of-order finals, duplicate/stale events, overflow, capture loss, WebSocket disconnect, rate limit, expired session, bad credentials, handshake 401, configuration timeout, explicit commit, cancellation and Stop. It uses synthetic PCM and fake sockets only. Existing native and managed tests remain part of the same solution. Offline success does not establish actual API response shape or transcript quality.

## Optional operator-gated live rehearsal — NOT RUN by CI

This sends real microphone audio and a synthetic SAPI speaking process to two paid OpenAI transcription sessions. It needs an operator present to choose a fresh microphone endpoint ID, listen for the synthetic speech, check source health and speak a short test phrase. The harness refuses to start without capture/playback acknowledgements, an explicit paid-API flag, a 8–20 s duration, an estimated cost cap at or above the two-stream estimate, and an available `OPENAI_API_KEY`. It reads the local ignored `.env.local` only in memory when no environment variable is set. It prints transcript text to the terminal for immediate review and writes no transcript/audio file. The cost check uses the published $0.017 per minute per stream and is an estimate; use project-level spend controls for a billing ceiling.

```powershell
./artifacts/native/Cintra.Audio.Native.exe --inventory
dotnet run --project tests/SttRehearsal -c Release --no-restore -- `
  --mic '<fresh microphone endpoint ID>' --seconds 15 --max-cost-usd 0.02 `
  --acknowledge-capture --acknowledge-playback --approve-paid-api
```

Do not run the paid rehearsal until the owner explicitly approves that run. The harness targets the SAPI process tree as a synthetic `RemoteApp`, not Zoom or Teams. A lack of remote text from a tone or unsupported renderer topology is a failed/inconclusive rehearsal, not compatibility proof. Record Windows build, audio device configuration, observed levels/gaps, provider session behavior and any source mismatch without retaining private speech.

## Still unverified

- Actual `gpt-live-transcribe` handshake, event timing, account access, rate-limit behavior and transcript quality. The adapter follows the current [Realtime transcription guide](https://developers.openai.com/api/docs/guides/realtime-transcription) and [Realtime event reference](https://developers.openai.com/api/reference/typescript/resources/realtime); no paid request was made during implementation.
- Real local microphone plus synthetic spoken loopback through the optional rehearsal, physical device changes, acoustic echo and long-duration drift.
- Zoom/Teams meetings and unscheduled direct calls. Those require the separate anonymized OS/app-version/process-tree hardware matrix in `docs/native-audio.md` before any compatibility claim.
