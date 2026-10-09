# MC-003–005 implementation scope

- Base: accepted main 274a838214f02dae8c0c131fb2c4aaee41a88e2b; clean checkout; branch orion/native-windows-audio.
- Native Windows x64 C++20 helper owns WASAPI, independent workers, conversion, notifications and resource lifetime.
- Managed Audio implements frozen IAudioCaptureSession, version-1 envelopes and authenticated current-user named pipes.
- Files: src/MeetingCompanion.Native, src/MeetingCompanion.Audio, tests/AudioIntegration, tests/Native, tests/AudioHarness, scripts/native-*, CI and audio documentation. Contracts remain unchanged.
- Tests: format conversion, timestamp accounting, bounded queues, invalid inputs, cancellation, pause/resume/stop, source exit, helper crash and real synthetic renderer isolation.
- Dependencies: Windows SDK C++/WinRT Windows.Data.Json (OS JSON implementation, windowsapp.lib), existing .NET/xUnit; no third-party native package.
- Mic is pinned by endpoint ID; process is pinned by an open handle/creation time. No automatic source replacement or unisolated fallback.
- Failure modes: denied COM/device/process access, activation timeout/late result, unsupported format, stale timestamp, driver blocking, endpoint loss and PID exit.
- No retained audio, STT/AI/UI changes, speaker-name attribution or claimed Zoom/Teams results.
