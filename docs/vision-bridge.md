# MC-009 manual vision bridge

`ManualVisionBridge` implements the orchestration for one operator-initiated image using frozen `IVisionReasoner`, `VisionRequest`, `ReasoningResult` and `Suggestion`. It has no screenshot scheduler, STT dependency, browsing, command execution, file logger, or automatic retries. Desktop wiring belongs to MC-012 and is not implemented here.

## MC-012 handoff

1. Construct one bridge per active conversation using its existing `RollingConversationStore`. Construct a separate `OpenAiVisionReasoner` with `OpenAiVisionReasoner.CreateClient()`, a credential delegate, and explicit `operatorEnabled: true` only after API/vision consent. Keep the client alive for that bridge and dispose it at session end. Do not attach logging/automatic retry/redirect handlers. STT credentials/consent alone do not enable vision.
2. On a manual Snapshot button/hotkey action only, obtain MC-008's `SnapshotResult`. Show the selected source and image preview, and obtain explicit image/API consent with an approved estimate. Transfer ownership of `SnapshotResult.Png` to `AnalyzeManualAsync(metadata, png, new VisionConsent(true, true, approvedEstimateUsd), token)`. The original byte array is zeroed even on rejection; do not reuse it. Desktop owns and must release the WPF preview independently.
3. The bridge calls `store.GetSnapshot(metadata.CapturedAtMs)` once, atomically obtaining the context and gap view. It filters both captured intervals and receipt UTC against the store's session clock; no later context is fetched when reasoning completes. It preserves retained stream/turn/event IDs and attribution. An existing matching `SnapshotAdded` event ID is reused; otherwise the bridge applies a metadata-only image evidence event using the existing store API. No image bytes enter the store. At very old capture times the store may already have evicted this metadata; use the returned card's screenshot provenance only in the current manual action, never as retained history.
4. Handle every outcome without changing capture/STT health. `busy` rejects concurrent requests with no queue. `vision_consent_required`, budget/size limits and stopped context send nothing. `vision_authentication_required` requires a new adapter after credential repair. `vision_rate_limited_no_retry` blocks new requests for the bounded Retry-After interval. Refusal, incomplete/malformed response, timeout, transport failure and unknown billing status yield no card. Never replay the same screenshot after an admitted attempt; a deliberate fresh manual capture is required. There is no background retry or resend.
5. Render/copy the returned card only. Commands are limited to a reviewed grammar: `docker ps`, `kubectl version --client`, and the explicit templates `kubectl get pods --namespace <namespace>` / `kubectl logs <pod> --namespace <namespace>`. Required identifiers remain placeholders. No shell or tool execution interface exists. Disable copying templates as executable actions until the operator understands the missing inputs. Every card expires at capture time + at most 60 seconds; discard expired cards and cards from a stopped session.
6. On Pause cancel the current invocation and block further manual sends while paused. On Stop/Dispose call `bridge.Stop()` first, dispose the client and clear the caller-owned conversation store/preview as part of overall session shutdown. Stop is terminal; resuming a stopped session requires a new bridge. It synchronously zeroes owned image and serialized HTTP bytes, drops its context/evidence references, cancels the request, returns promptly even for an adapter ignoring cancellation, and suppresses late output. The bridge never clears another owner's conversation store on its own.

Strings and platform HTTP/TLS buffers cannot be securely erased by managed code. No persistent image/transcript file is created. Custom adapters must honor cancellation and must not retain request objects or copy/log buffers. Faulty adapters can retain their own copies; bridge-owned references and mutable buffers are still cleared, and admitted attempts are capped. The fake adapter intentionally retains a request in tests solely to inspect zeroed bytes.

## Bounds, grounding and budget

- One PNG, at most 4 MiB / 4,194,304 pixels, header dimensions matching metadata; crop dimensions must match encoded dimensions. Negative desktop coordinates are valid. MC-008 owns capture/blank-frame/encoding checks; the bridge does not decode/recompress screenshots or pretend to verify OCR legibility. Unreadable provider results are explicit incomplete outcomes.
- At most eight final turns of 512 characters each, eight gaps, eight source changes, and four statements per issue field, each 512 characters. Partial turns are omitted and flagged. The serialized context is limited to 24,000 characters; exceeding the policy's cap rejects the request. Interrupted turns, uncertain attribution, clipped text and possible omissions/evictions are explicit. MC-007 has no eviction ledger, so complete history is never claimed. Issue statements whose evidence is absent from the transmitted turns are omitted.
- One 20-second request by default (configurable up to 60 seconds), 2048 output tokens, 64 KiB response, four observations/hypotheses/steps/commands, 384 characters per model text, 3000-character card body. Strict JSON schema is complemented by local shape/length/enum/evidence/command checks. Screenshot observations must cite the current image event; call observations must cite transmitted final events. Unknown IDs are rejected. Visible observations and unverified explanations are separated. These structural checks do not prove a model's visual claims correct; operator review remains necessary.
- Default ten admitted attempts and USD 0.10 estimated session budget. Per-action consent must cover the conservative estimate. Every admitted attempt reserves its estimate permanently, including uncertain failures; no automatic refund/resend. Estimate uses uncached published token prices for the pinned model, UTF-8 text bytes, 8192 instruction/schema reserve tokens, 8192 image reserve tokens, and maximum output. This exceeds the documented image patch ceiling but is an estimate, not an account billing cap. Actual account spending controls remain separate. Only numeric usage counts are exposed; no provider error bodies, credentials, image data or transcripts are logged.

## Provider contract checked 2026-10-10

The official [GPT-4.1 mini model page](https://developers.openai.com/api/docs/models/gpt-4.1-mini) documents image input, Responses and Structured Outputs, including the pinned `gpt-4.1-mini-2025-04-14` snapshot and USD 0.40 input / 1.60 output per million text tokens. [Images and vision](https://developers.openai.com/api/docs/guides/images-vision) documents `input_image`, data URLs, detail and image patch accounting. [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses) documents Responses `text.format` / strict JSON schema and refusals. The adapter sends one `input_text` plus one inline PNG `input_image` to `POST https://api.openai.com/v1/responses` with `store:false`, no tools and no background processing. A separate non-STT adapter is essential.

Documentation compatibility is verified; account access, actual model/API behavior, image-reading quality, costs and real provider integration are **not verified** by offline fake tests. No live request or key provisioning is performed in standard tests or CI.

## Reproducible validation

From the repository root in PowerShell:

```powershell
./scripts/build.ps1
./scripts/native-build.ps1
dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore
./scripts/test.ps1
dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts
git diff --exit-code -- docs/contracts
```

The solution includes `tests/Ai` in existing Windows offline CI. The standalone `tests/AiLive` program is compiled but never executed by CI. Focused offline tests:

```powershell
dotnet test tests/Ai/MeetingCompanion.Ai.Tests.csproj -c Release --no-restore
```

Optional **paid** operator smoke test, only after separate approval and secure local credential setup:

1. Manually capture a non-sensitive fixture window showing `FixtureError E123: service unavailable`. Save this intentional test fixture PNG outside the repository; no transcripts or real environment details are needed. Keep it below the byte/pixel limits. This harness does not capture the screen automatically.
2. Make `OPENAI_API_KEY` available securely to the process, without putting it in a command or log. Review the estimate, current published prices and provider retention policy. The API upload is explicit and may be billed.
3. Run once, without piping output to a persistent log:

```powershell
dotnet run --project tests/AiLive -c Release --no-build --no-restore -- --acknowledge-api-and-image-spend --manual-png '<absolute path to manually created fixture.png>' 0.02
```

4. Confirm one completed request, numeric usage, an observation citing the fixture rather than invented environment details, an explicitly unverified explanation, and no commands executed. A blank/unreadable image should return Incomplete. Ctrl+C exercises cancellation. Record only anonymized outcome/usage, model and API date; do not retain screenshot contents or keys in repository evidence. Failure never triggers a second POST automatically. This test was **not executed** for this implementation.

Zoom/Teams meetings/direct calls, actual screenshots/hardware and overall desktop session integration are outside MC-009's offline acceptance evidence and remain unverified.
