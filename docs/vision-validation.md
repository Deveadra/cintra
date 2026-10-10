# MC-009 / issue #10 validation evidence

Validated on 2026-10-10, branch `orion/mc-009-vision-bridge`, based on accepted main `e66482adf6e1f21a57da0f1b6183a729e9194ec2`.

## Done

Manual image/context orchestration, a separate Responses vision adapter, per-action consent, estimated budget/request/size/output/time bounds, capture/receipt-time context filtering, provenance/uncertainty/gap handling, typed result and reviewed command validation, Stop/cancellation cleanup and late-completion suppression. Includes an optional operator-gated live harness and MC-012 handoff in [vision-bridge.md](vision-bridge.md). Frozen contracts/schemas and Desktop/Core/STT/Platform/native implementation are unchanged.

## Verified locally

| Exact command | Result |
| --- | --- |
| `./scripts/build.ps1` | Restore/build succeeded; 0 warnings, 0 errors; includes x64 WPF/native snapshot dependency and live harness compile |
| `./scripts/native-build.ps1` | Native CTest: 2/2 passed, 0 failed |
| `dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore` | Exit 0; no diagnostics after project-scoped formatting |
| `./scripts/test.ps1` | 192/192 managed tests passed, 0 failed, 0 skipped; offline synthetic replay `result: PASS` |
| `dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts` | Export succeeded |
| `git diff --exit-code -- docs/contracts` | Exit 0; no frozen-schema changes. Generated CRLF line-ending changes were normalized back to original LF |

Managed totals: Ai 50, AudioIntegration 11, Capture 18, Contract 27, Platform 31, Stt 16, Unit 39. Final build/test verification was repeated after the formatter changed whitespace. The initial compile/fixture errors and one test assertion about a copied rather than owned HTTP buffer were corrected before these final passing runs.

Vision regression coverage includes exact one-image/no-tools/store-false wire shape; capture-time and receipt-time filtering; IDs/attribution/crop metadata; atomic detached context; missing/evicted/clipped/issues/partial/gap/health interruption flags; concurrent busy rejection; pre-cancellation; cancellation and Stop during a non-cooperative HTTP handler; immediate owned PNG/HTTP-buffer zeroing; late result suppression; deterministic timeout and Retry-After expiry; no uncertain-request replay; conservative budget reservations and request caps; 401 authentication latch; 429 cooldown; network/500 failures; missing credentials/consent; malformed/refused/incomplete/unreadable/oversized output including unknown response length; invalid evidence/provenance/text shape/unknown fields; command/identifier validation; existing/new snapshot evidence IDs; and throwing adapter cancellation callbacks.

No provider call, microphone capture, screen capture, live audio playback, key creation, or paid request was made for this validation. Fake HTTP credentials are fixed non-secret placeholders. Published model/image/Structured Outputs compatibility was checked against official OpenAI documentation; that does not establish runtime account access or model accuracy.

## Not verified / risks

Actual provider response/schema acceptance, visual reading quality, actual billing, Desktop integration (owned by MC-012), real hardware/screenshots and Zoom/Teams meetings/direct calls are not verified. Managed strings/platform transport buffers cannot be securely erased; custom adapters must not retain their own copies. Budget is an estimated admission guard, not a provider account spend cap. The store lacks a complete eviction ledger, so context incompleteness is deliberately flagged. These limitations are not represented as successful integration tests.

## Next safest action

MC-012 should wire the manual button/hotkey and source preview/consent, render outcome codes independently of STT health, discard expired/stopped-session cards, and clear its own store/preview on Stop. The optional paid fixture smoke test in the handoff requires separate operator approval and secure credentials. Standard CI remains entirely offline. PR/remote CI status must be verified on GitHub rather than inferred from these local results.
