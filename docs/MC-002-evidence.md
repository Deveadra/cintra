# MC-002 evidence and remaining acceptance gates

Date: 2026-10-10, America/Chicago. Branch orion/mc-002-call-source-discovery.
Starting accepted main: 432d81c21b7136ecc92d2b942e64ff9e2f83026b.
Implementation and MC-012 integration: [MC-002-call-source-discovery.md](MC-002-call-source-discovery.md).
Historical matrices remain historical: [native audio](native-audio-evidence.md),
[research matrix](research/windows-audio-test-matrix.md).

## Environment checked before edits

Windows x64 build 26200.9457, 25H2; .NET SDK 10.0.401;
Visual Studio Build Tools 2026 18.10.2; MSVC 19.51.36260.0;
Windows SDK Include 10.0.26100.0. CMake/Ninja available in the Visual Studio developer environment.
The baseline ./scripts/native-build.ps1 built and passed CTest 2/2.
Baseline ./scripts/build.ps1 restored/built the accepted main with 0 warnings, 0 errors.
The stale primary checkout and its main were not edited or advanced. Git fetch obtained accepted
Cintra main, and a new isolated worktree/branch was created at that exact commit.

## Actual read-only discovery, first run

Command: dotnet run --project tests/PlatformWindows -c Release --no-build --no-restore.
Result: exit 0. The tool launched only itself; no audio was captured or call joined.

- OS build 26200; x64 true; processLoopbackSupported true; ProcessesComplete true; Diagnostics [].
- 370 processes observed; 220 with readable creation/path identity.
- Render endpoint: Speakers (Realtek(R) Audio). Capture endpoint: Microphone (Realtek(R) Audio).
- Four render sessions, zero active sessions.
- Zoom candidates: 0. No running Zoom process was observed.
- Teams candidates: 1. Root ms-teams.exe PID 16344.
- Observed tree: [9052, 15132, 15672, 15824, 15904, 16344, 16452, 16552, 16852, 17000, 17320].
- One mapped inactive Teams-tree session; RenderActive false; active renderer PIDs [].
- Validity Ready / process_tree_valid_audio_unverified; this proves metadata selection eligibility only.
- Manual self PID 30920: process tree [30920]; Probe Silence /
  no_active_render_stream_audio_unverified; identity_pinned_no_capture; stream RemoteApp.

PIDs/counts above are time-specific observations, not hardcoded constants.
The first run did not print per-member names/versions; the harness was extended for that evidence.
Unqueryable system/privileged processes are not silently treated as selectable.

## Deterministic validation

Initial focused command:
dotnet test tests/Platform/MeetingCompanion.Platform.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory TestResults.
Result: 29 passed, 0 failed, 0 skipped, 527 ms, exit 0.
Subsequent review added shared-session creator-outside-tree and exit-during-pin regression tests.

Coverage includes Zoom/modern and classic Teams roots; observed descendants/active renderer;
exclusion of unrelated playback process metadata; startup/exit; root PID reuse with same/different
executable; child PID reuse; renderer start/stop/replacement; session replacement; hold/resume;
multiple independent app roots; nested matching processes; stale reused-parent edges; unreadable
child/root identity; stale session ownership; shared sessions; missing devices; permission/inventory
failure; timeout distinguished from exit; manual silent clients/browser warning; candidate mutation;
pin race; lease lifetime and cancellation. These are offline fixtures, not real-call tests.

## Final validation and additional live evidence

| Command | Actual result |
|---|---|
| ./scripts/build.ps1 (after final source changes) | Build succeeded; 0 warnings, 0 errors; exit 0 |
| ./scripts/test.ps1 | Platform 31, Unit 23, Contract 27, STT 16, Capture 18, Audio integration 11 passed; 126 total, 0 failed, 0 skipped; exit 0 |
| PlaybackHarness (run by test.ps1) | offline_synthetic; frames 2; result PASS; exit 0 |
| ./scripts/native-build.ps1 (baseline; native unchanged) | 2/2 CTest passed; exit 0 |
| dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore | No output; exit 0 |
| dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts | Exit 0; git diff --exit-code -- docs/contracts exit 0 (no content drift) |
| git diff --check | No output; exit 0 |
| git diff --exit-code -- src/MeetingCompanion.Contracts src/MeetingCompanion.Core src/MeetingCompanion.Desktop src/MeetingCompanion.Audio src/MeetingCompanion.Native | No output; exit 0 |

Second live command:
dotnet run --project tests/PlatformWindows -c Release --no-build --no-restore -- --select-pid 16344.
Result: exit 0, identity_pinned_no_capture for ms-teams.exe root 16344, RemoteApp.
383 processes, 226 readable identities, 4 sessions, 0 active; Diagnostics [].
Zoom 0 candidates; Teams 1 candidate, same 11-member tree.

| PID | Parent PID | Executable | Observed file version | Session evidence |
|---|---|---|---|---|
| 16344 | 7536 | ms-teams.exe | 26260.1701.5139.3736 | Root; no direct session |
| 15824 | 16344 | msedgewebview2.exe | 154.0.4258.62 | WebView parent |
| 15672 | 15824 | msedgewebview2.exe | 154.0.4258.62 | No session |
| 15132 | 15824 | msedgewebview2.exe | 154.0.4258.62 | No session |
| 15904 | 15824 | msedgewebview2.exe | 154.0.4258.62 | No session |
| 9052 | 15824 | msedgewebview2.exe | 154.0.4258.62 | No session |
| 16452 | 15824 | msedgewebview2.exe | 154.0.4258.62 | No session |
| 16552 | 16344 | ms-teams.exe | 26260.1701.5139.3736 | No session |
| 17000 | 15824 | msedgewebview2.exe | 154.0.4258.62 | No session |
| 16852 | 16344 | ms-teams.exe | 26260.1701.5139.3736 | No session |
| 17320 | 15824 | msedgewebview2.exe | 154.0.4258.62 | Inactive, single process, nonsystem |

Creation FILETIME was successfully read for every member; for example root 16344:
134360440358846990; session owner 17320: 134360440448384909. These are observed identities,
not permanent selectors. No active Teams media renderer or call was proved.

Implementation review checked identity/time/path matching, Toolhelp parent reuse, activity vs
signal semantics, defensive candidate copies, shared-session ambiguity (including creator outside
tree), failed pin cleanup, cancellation and MC-012 startup/stop ownership. Two added regression
tests cover review findings. Independent PR approval remains a merge gate; no automatic merge.

## Real-call acceptance matrix

| Scenario | Current evidence | Result |
|---|---|---|
| Zoom desktop meeting | No running Zoom observed; no call joined | NOT VERIFIED |
| Zoom ad hoc direct call | Same; calendar-independent fixture path only | NOT VERIFIED |
| Teams desktop meeting | Running app tree and inactive session observed; no call joined | NOT VERIFIED |
| Teams ad hoc direct call | Same; calendar-independent fixture path only | NOT VERIFIED |
| Renderer replacement / app restart / PID reuse | Deterministic fixture tests | Real application behavior NOT VERIFIED |
| Hold/resume/transfer/device switch | Metadata hold/resume fixture only | Real application behavior NOT VERIFIED |
| Remote speech vs microphone and unrelated media | No audio capture in MC-002 | NOT VERIFIED |
| Browser tab isolation / second hardware configuration | Not exercised | NOT VERIFIED |

The original MC-002 real Zoom/Teams call gate remains open. Discovery implementation must not
be reported as product release acceptance. No fabricated call compatibility, latency, hardware
leak, voice separation or provider validation claims are made.
