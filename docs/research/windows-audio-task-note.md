# Agent B scope, 2026-10-09

- Preparation for MC-002 (Platform/QA; depends on MC-001), MC-003 (Native Windows Audio; depends on MC-001), MC-004 (Native Windows Audio; depends on MC-001 and MC-002).
- Starting checkout: clean, detached HEAD `7365340b66f45468a3a0cedd35a9888367bee7d9`; local main pointed to the same planning commit.
- Research branch: `orion/agent-b-windows-audio-spike`. No commits, pushes, merges, or production task status changes planned.
- Only edit `spikes/windows-audio/` and `docs/research/`; do not consume or alter Agent A's unfrozen contracts.
- Use .NET 10 x64 solely as a standalone native COM/PInvoke test host; no C++ compiler or Windows SDK found in PATH/conventional locations. Production C++20 remains the handoff recommendation.
- Interfaces: IMMDeviceEnumerator, IMMDevice, IAudioSessionManager2, IAudioSessionControl2, IAudioClient, IAudioCaptureClient, ActivateAudioInterfaceAsync, Toolhelp32 process snapshots.
- Diagnostics default to inventory only; capture requires explicit sources and acknowledgement; maximum 30 seconds, Ctrl+C, levels/timestamps only, no audio files/network.
- Tests: offline PCM metering and health/fault policy, CLI rejection, native inventory, bounded activation and capture where devices permit. Real calls are a separate manual gate.
- Expected failures: inaccessible COM/services/process metadata, absent endpoint, unsupported format/build, invalid/stale PID, silent wrong tree, callback timeout, device invalidation, acoustic echo, shared browser renderers.
- No automatic fallback, source substitution, application launch, meeting join, or device routing changes.
