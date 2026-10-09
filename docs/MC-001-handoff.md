# MC-001 — foundation, CI and typed contracts

## Done

Implemented on `orion/mc-001-foundation`, based on HEAD
`7365340b66f45468a3a0cedd35a9888367bee7d9`. Initial checkout was clean and contained only the
implementation pack. No commit, PR, push, merge, rebase or main-branch change was made.

Added an 11-project .NET solution: eight managed production boundaries and three test/replay projects.
Contracts and Core contain implemented code; Desktop is an explicitly inactive WPF shell; Audio,
Stt, Ai, Capture and Platform are dependency boundaries awaiting their respective backlog tasks.
Native code and audio experiments were not created or modified.

Implemented version-1 audio/source/health contracts, transcription and conversation events,
evidence/context DTOs, suggestions and display-only commands, platform interfaces and separate
transcription/text/vision service interfaces. IPC framing validates versions, payload size and
semantics and supports fragmented reads, clean/partial EOF, deadlines and cancellation.

Core includes a deterministic thread-safe session state machine with capture/STT health gating,
expiry, degradation, explicit recovery, pause/cancel/stop, operation timeout and retry policy.
Offline xUnit infrastructure, a finite injectable synthetic audio adapter, virtual-time lifecycle
replay, build/test/environment scripts, schema export and Windows GitHub Actions CI are present.

## Verified — local Windows evidence, 2026-10-09

Environment: Windows `10.0.26200`, x64; .NET SDK `10.0.401`, .NET/WindowsDesktop runtime `10.0.12`.
No Windows SDK include directory found at `C:\Program Files (x86)\Windows Kits\10\Include`.
This did not block the managed WPF build.

Exact commands and observed results:

| Command | Result |
| --- | --- |
| `./scripts/check-environment.ps1` | Exit 0; SDK/runtime/OS information above |
| `./scripts/build.ps1` | Restore and Release build of all 11 projects succeeded; 0 warnings, 0 errors; initial build 12.99 s |
| `dotnet build MeetingCompanion.slnx -c Release --no-restore -t:Rebuild` | Full rebuild succeeded; 0 warnings, 0 errors; 7.29 s |
| `dotnet build MeetingCompanion.slnx -c Release --no-restore` | Final build after fixture correction: 0 warnings, 0 errors; 2.27 s |
| `dotnet test MeetingCompanion.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults` | Final run: Unit 23 passed, 0 failed, 0 skipped (140 ms); Contract 27 passed, 0 failed, 0 skipped (410 ms) |
| `dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore` | PASS; two independent synthetic frames; expected lifecycle transitions |
| `dotnet format MeetingCompanion.slnx --no-restore` | Formatting applied, exit 0 |
| `dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore` | Exit 0; no changes required |
| `dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts` | Exit 0; three versioned schemas exported; repeat export matched SHA-256 hashes |
| `git diff --check` | Exit 0 |
| `git diff --exit-code -- README.md IMPLEMENTATION_PLAN.md AGENTS.md AGENT_BACKLOG.json CHECKSUMS.sha256` | Exit 0; original pack unchanged |

`./scripts/test.ps1` runs the test and replay commands above and stops on any failure.
The final local TRX files are ignored build artifacts in `TestResults`; CI uploads its own TRX results.
All 50 tests run without paid APIs, credentials, native capture or meeting applications.
Cases include every event/IPC variant, exact enum labels, unsupported versions, unknown/missing/null
fields, size/format validation, separate streams, fallback acknowledgement, automatic-command safety,
fragmented/truncated IPC, cancellation/deadline failures, stale health, provider degradation,
recovery, pause, explicit failure, timeout, cancellation and 100 start/stop policy cycles.

Replay output:

```json
{"mode":"offline_synthetic","frames":2,"transitions":["0:SelectingSource","0:Ready","0:Starting","20:Listening","2020:Degraded","2021:Recovering","2022:Listening","2023:Paused","2024:Stopping","2025:Stopped"],"result":"PASS"}
```

An expanded test initially compared UTC `Z` and `+00:00` as different strings; its fixture was
corrected to canonical serialized UTC and all tests were rerun. No application timestamp was lost.
Original pack checksums assume LF; the Windows checkout uses CRLF. All four listed checksums pass
after LF normalization, and Git reports no original-file differences. No pack files were rewritten.

The restricted sandbox caused `dotnet --info` to fail while reading Windows services and restore
to exit without diagnostics. Approved broader execution succeeded for restore/build/format/tests;
SDK/runtime enumeration works with the read-only environment script. These are environment limits,
not suppressed build failures.

## Architecture decisions

- .NET 10, net10.0 core/contracts and net10.0-windows x64 WPF; no native/project API dependencies in Core.
- Allowlisted JSON discriminators, string enums, required fields, schema version 1 and semantic validators.
- Four-byte little-endian length plus bounded JSON/base64 PCM; finite deadlines; abandon interrupted frames.
- Distinct mic/incoming identities and explicit unisolated fallback; no individual remote-speaker claims.
- Capture-time monotonic timestamps; provider receipt time is not conversation ordering.
- Pure lifecycle policy separate from actual resource ownership; fresh capture/STT health before Listening.
- Separate text-only STT, text reasoning and still-image reasoning boundaries; commands remain display-only.

See `ADR/0001-foundation.md` for rationale and `contracts/README.md` for the audio implementer's protocol.

## Not verified

- GitHub-hosted CI has not run: pushing was prohibited. Workflow and equivalent local commands are present.
- Fresh-machine installation, WPF interactive launch/accessibility, signing and packaging.
- Native build/toolchain, WASAPI, named-pipe user ACLs/peer validation, helper cleanup or physical device release.
- Live transcription/reasoning model support, VAD, API authentication or provider recovery.
- Zoom/Teams meetings and direct calls, renderer isolation, Bluetooth/device switching and capture latency.

No compatibility matrix is marked green and no hardware/meeting test is claimed.

## Risks / implementation boundaries

The FSM removes permission to send/generate; it cannot itself cancel sockets or release a native
device. MC-005/006/012 must perform and acknowledge cleanup, gate each transport, discard old-session
callbacks and call Tick periodically. The two-second queue cap is an adapter obligation, not a
queue implementation in MC-001. Pipe creation and access controls are not implemented by the stream codec.

Typed context/evidence references and command risk labels do not prove model grounding or command safety.
MC-007/011 must validate evidence, ordering and output policy. Exported JSON Schemas describe structure;
runtime validators enforce additional bounds and cross-field semantics. Full quality replay metrics
remain with MC-010/011/016; this harness is foundation-only synthetic replay.

## Next safest action

Review/freeze the v1 contracts and ADR, then have the audio owner implement IAudioCaptureSession and
the native framing/source/health protocol in MC-003–005. Start with the no-provider dual-level-meter
proof from plan section 12. Preserve explicit gap/source-change reports. Validate named-pipe ACLs,
physical stop/pause cleanup and real Zoom/Teams meeting/direct-call behavior before making compatibility
claims. The current working-tree changes are ready for review; no remote publication was performed.

## Files created

The inventory below includes all MC-001 source, configuration, tests, schemas and documentation.
No original tracked files were modified. Build output, package caches and TRX files are ignored.

```text
.editorconfig
.gitattributes
.github/workflows/ci.yml
.gitignore
Directory.Build.props
MeetingCompanion.slnx
NuGet.Config
docs/ADR/0001-foundation.md
docs/BUILD.md
docs/MC-001-handoff.md
docs/MC-001-implementation-note.md
docs/contracts/README.md
docs/contracts/events-v1.schema.json
docs/contracts/ipc-v1.schema.json
docs/contracts/suggestion-v1.schema.json
global.json
scripts/build.ps1
scripts/check-environment.ps1
scripts/run-dev.ps1
scripts/test.ps1
src/MeetingCompanion.Ai/MeetingCompanion.Ai.csproj
src/MeetingCompanion.Audio/MeetingCompanion.Audio.csproj
src/MeetingCompanion.Capture/MeetingCompanion.Capture.csproj
src/MeetingCompanion.Contracts/Audio.cs
src/MeetingCompanion.Contracts/ContractJson.cs
src/MeetingCompanion.Contracts/Conversation.cs
src/MeetingCompanion.Contracts/Interfaces.cs
src/MeetingCompanion.Contracts/IpcFraming.cs
src/MeetingCompanion.Contracts/MeetingCompanion.Contracts.csproj
src/MeetingCompanion.Contracts/Primitives.cs
src/MeetingCompanion.Contracts/Suggestions.cs
src/MeetingCompanion.Core/MeetingCompanion.Core.csproj
src/MeetingCompanion.Core/SessionStateMachine.cs
src/MeetingCompanion.Desktop/App.xaml
src/MeetingCompanion.Desktop/App.xaml.cs
src/MeetingCompanion.Desktop/MainWindow.xaml
src/MeetingCompanion.Desktop/MainWindow.xaml.cs
src/MeetingCompanion.Desktop/MeetingCompanion.Desktop.csproj
src/MeetingCompanion.Platform/MeetingCompanion.Platform.csproj
src/MeetingCompanion.Stt/MeetingCompanion.Stt.csproj
tests/Contract/IpcFramingTests.cs
tests/Contract/MeetingCompanion.Contract.Tests.csproj
tests/Contract/WireContractTests.cs
tests/Directory.Build.props
tests/PlaybackHarness/MeetingCompanion.PlaybackHarness.csproj
tests/PlaybackHarness/Program.cs
tests/PlaybackHarness/ReplayAudioSession.cs
tests/Unit/MeetingCompanion.Unit.Tests.csproj
tests/Unit/SessionStateMachineTests.cs
```
