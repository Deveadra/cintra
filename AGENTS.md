# AI Agent Operating Instructions — Meeting Companion

Read `IMPLEMENTATION_PLAN.md` first. This file is an implementation handoff, **not an assertion that the repository already contains working code**.

## Canonical constraints

- Windows 11 first, desktop Zoom/Teams meeting **and** direct-call compatibility mandatory; other apps via audibly verified source-picker/fallback.
- Two physically separate streams: local microphone and selected application's rendered audio. Do not infer the identity of individual remote speakers from mixed audio.
- `gpt-live-transcribe` is an independent, client-VAD-driven STT session for each stream. Typed suggestion generation uses a separate reasoning adapter. Screenshots use a vision-capable reasoning call, not STT sessions.
- Manual screenshots only: one button / global hotkey; source = foreground window, region, or display; no continuous screen monitoring.
- The app does not speak into meetings, execute suggested commands, or modify user applications in MVP.
- No long recordings, no default retained screenshots/transcripts, no hidden audio source capture, no API tokens in repository/logs.
- Owner handles external organizational approval. Build meaningful operator controls, but do not stall implementation on that question.

## Before editing

1. Inspect workspace: actual repo, branch, HEAD, working tree, installed Windows SDK, and available tests.
2. Read task ID in `AGENT_BACKLOG.json`, its owners and dependencies, and corresponding full section in `IMPLEMENTATION_PLAN.md`.
3. State files touched, interfaces used, tests to add, and expected failure modes. Implement only a scoped task.
4. Never overwrite other agents' changes. Do not reset, force push, rebase, delete, or casually replace working code.
5. Use fakes/replay in standard CI, and label real platform validation separately.
6. Do not assume a model supports a feature merely because another model does. Check actual model/session compatibility.

## Before handing off

- `git status --short` reviewed; no unintended changes.
- Build, format and tests run (report exact commands and output, or explicit inability to execute).
- All degraded/reconnect/timeout/cancel paths tested, not only happy path.
- Report reality as `Done / Verified / Not verified / Risks / Next safest action`.
- Reference branch and PR/commit if actually created; never fabricate these.
- For Zoom/Teams tasks, attach an anonymized reproducible hardware/OS/app-version test matrix before claiming compatibility.

## Suggested first assignments

- **Agent A:** `MC-001` contracts/project scaffolding.
- **Agent B:** prepare Windows process-tree investigation strategy and fixtures for `MC-002/MC-004`, without changing unfrozen contracts.
- **Agent C:** prototype acceptance/evaluation fixtures for `MC-010/MC-011` offline; start production code only after `MC-001` publishes contracts.
- **Agent D:** screenshot UX source/capture design for `MC-008`; implementation after shell/interfaces are available.

Keep the first integration demo as described in §12 of `IMPLEMENTATION_PLAN.md`. A reliable narrow demo is preferable to a broad nonfunctional scaffold.
