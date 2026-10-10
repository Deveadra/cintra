# MC-006 implementation note

- Base: accepted `main` `e46bc478e5fddb644eb5031bc9718f214d332098`; feature branch `orion/mc-006-dual-stt`.
- Use frozen `IAudioCaptureSession.ReadAllAsync`, `ITranscriptionSession`, `AudioFrame`, `CaptureGap`, and `ConversationEvent` v1 contracts without schema edits.
- Own `src/MeetingCompanion.Stt`, focused `tests/Stt`, solution test wiring, and `docs/stt.md`; `.gitignore` excludes the local API key file.
- Two independent WebSocket transports use `gpt-live-transcribe`, PCM16 mono 24 kHz, local VAD, and one commit at each completed speech turn.
- Audio queues are bounded per stream; overflow, source changes, connection loss, and uncertain turns emit gaps and degraded health.
- Offline fake sockets and replayed capture messages test speech, silence, overlap, fragmentation, event ordering, reconnect, credentials, cancellation, and cleanup.
- No paid API request, Zoom/Teams call, screenshot, reasoning adapter, or long-lived recording is in this change.
- Real provider schema behavior, transcript quality, acoustic echo, and app compatibility remain manual validation gates.
