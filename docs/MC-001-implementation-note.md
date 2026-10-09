# MC-001 implementation scope

- Baseline: clean, planning-only checkout at `7365340b66f45468a3a0cedd35a9888367bee7d9`; no existing tests.
- Owner: Architect/Contracts; no dependencies. Branch: `orion/mc-001-foundation`.
- Preserve the five original implementation-pack files byte-for-byte; publish status in this task's handoff instead.
- Add Contracts/Core, a deliberately inactive WPF shell, managed adapter project boundaries, test/replay projects, scripts and CI.
- Freeze v1 audio/source/health, transcription, conversation, suggestion, platform and lifecycle interfaces.
- Test wire compatibility, malformed/oversized/unknown-version IPC, fragmented reads, EOF, cancellation and deadlines.
- Test lifecycle health expiry, degraded/recovery, pause, stop, retry, timeout and repeated cycles with virtual time.
- No native capture, provider calls, credential access, process probing, or experiments owned by the audio agent.
- Installed .NET SDK: 10.0.401, Windows Desktop reference/runtime packs available. Windows SDK include directory absent.
- Risks: native timing/ACL enforcement, API compatibility and real Zoom/Teams behavior remain separate validation gates.
