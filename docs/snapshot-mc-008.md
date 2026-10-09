# MC-008 manual Windows snapshots

## Implementation note and ownership

- Start: clean isolated worktree on accepted main e46bc478e5fddb644eb5031bc9718f214d332098; branch orion/mc-008-manual-snapshot.
- Verified before implementation: .NET SDK 10.0.401, Windows SDK 10.0.26100.0, MSVC 19.51, C++/WinRT headers, WGC IsSupported=true and hardware D3D11 creation HRESULT 00000000 on Windows 11 build 26200.
- Own Capture, screenshot hooks in Desktop, tests/Capture and tests/CaptureWindows, this document, and solution test registration.
- Frozen SnapshotMetadata, SnapshotSource, PixelRegion and ClockMapping are consumed unchanged. STT, AI, core and native audio helper remain unchanged.
- Backend: Windows.Graphics.Capture CreateForWindow/CreateForMonitor via a small C++/WinRT DLL in Capture/native. No GDI/PrintWindow fallback or alternative architecture.
- One manual request creates a frame pool/session, obtains one frame, copies GPU pixels, and closes frame/session/pool. Frame arrival and nonblocking GPU-read waits have a three-second deadline and cancellation event.
- Offline tests inject ISnapshotBackend and IHotkeyApi; opt-in Windows harness uses synthetic windows, real APIs, physical cursor/keyboard events and pixel comparisons.
- Failure cases include invalid source/crop/dimensions, all-black image, denied/protected/minimized/closed source, resize during capture, changed monitor layout, missing DLL, busy, timeout and cancellation.

## Operator controls

Run `dotnet run --project src/MeetingCompanion.Desktop -c Release` on an interactive Windows desktop.
Select last active external window, a window from the refreshed list, a region on a chosen monitor, or the chosen monitor. Click Snapshot or use the registered global hotkey. Default is Ctrl+Shift+S only when registration succeeds. Rebinding supports Ctrl+Shift plus any letter A-Z; collisions disable that hotkey and show a message while the button remains available. Settings are in-memory for this shell; persistent settings belong to MC-012.

The foreground hook remembers the external HWND before the shell takes focus; the hotkey also samples current foreground immediately. The shell and region selector request and read back WDA_EXCLUDEFROMCAPTURE. A failed exclusion request prevents capture and shows an error. Exclusion is a Windows compatibility mechanism, not a security guarantee.

Drag a region on the selected monitor; Esc or Cancel cancels. The PerMonitorV2 manifest and PointToScreen convert selection into physical pixels. Crop metadata uses monitor-relative physical pixel coordinates, not WPF DIPs; negative desktop origins are normalized by RegionGeometry. A region spans one selected monitor; cross-monitor stitched regions are intentionally unsupported. Changed display geometry requires refreshing/reselecting.

Preview and PNG bytes remain in memory until cleared, replaced, or the shell exits. Starting a request clears the old preview, so cancellation/error cannot present stale pixels as a success. There is no screenshot file writer, provider upload, AI vision call, scheduled screenshot, or background pixel capture. Source/window titles appear only in the UI/result and are bounded to 256 characters. Frame SystemRelativeTime supplies QPC-based captured-at time relative to the supplied ClockMapping.

## Reproducible validation

```powershell
./scripts/build.ps1
./scripts/native-build.ps1
dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore
./scripts/test.ps1
dotnet run --project tests/PlaybackHarness -c Release --no-build --no-restore -- --export-schemas docs/contracts
git diff --exit-code -- docs/contracts
dotnet run --project tests/CaptureWindows -c Release --no-build -- --smoke
```

The Windows harness is opt-in and must have an interactive desktop. It creates and cleans up a separate synthetic red-window process and an excluded green overlay. It temporarily moves the cursor and injects a hotkey/region drag/Esc. Do not operate the desktop while it runs. It writes no screenshot files and logs only synthetic fixture labels, dimensions and outcomes. Standard CI runs fakes and never invokes --smoke.

## Observed matrix (2026-10-09)

| Scenario | Evidence | Status |
| --- | --- | --- |
| Windows 11 25H2 build 26200.9457, x64, AMD Radeon(TM) Graphics driver 32.0.21030.29002, one 1920x1080 monitor, 125%/120 DPI | Real WGC/D3D11 capability probe and Windows harness | PASS |
| Click last foreground window while companion has focus | Assert actual focus transitions; red center pixel from separate fixture process | PASS |
| Selected-window global hotkey | Real registration, duplicate registration collision, injected Ctrl+Shift+Z, shell capture and red pixel | PASS |
| One-click monitor | Actual shell button and monitor dimensions | PASS |
| Region at 125% scaling | Actual modal selector, physical mouse drag, 100x100 PNG/preview and red center pixel | PASS |
| Overlay exclusion | Green pixel before exclusion; red pixel beneath same overlay after affinity request/readback | PASS |
| Negative desktop window X and moved/resized window | Real HWND moved to X=-50, resized, captured red pixels | PASS |
| Minimized and destroyed HWND | Actual Windows errors; five repeat captures after restore | PASS |
| Cancellation | Pre-cancel and native in-flight cancel; recovery; Esc clears region preview | PASS |
| Synthetic protected window | WDA_MONITOR test window yields access_denied | PASS |
| Exit | Registered hotkey becomes available after shell closes | PASS |
| Negative monitor origins/fractional geometry/reverse drag/clamping | Offline RegionGeometry fixtures | PASS, synthetic only |
| Two or more physical monitors, negative monitor origin, mixed DPI, moving across DPI boundaries | No second monitor available | NOT VERIFIED |
| Real DRM/protected application windows, access policy denial, GPU reset, resize during frame arrival, RDP/lock-screen, HDR and multi-GPU | Not physically exercised | NOT VERIFIED |
| Zoom/Teams screenshots during direct calls/meetings | Not exercised; no compatibility claim | NOT VERIFIED |

Final local command counts/results are recorded in the PR and completion handoff. Initial fixture-startup/focus harness failures were corrected; they were not capture passes.

## Limits and remaining integration

- Detected HDR output is rejected with unsupported_hdr rather than silently clipping into SDR; tone mapping is deferred. Spanning HDR/SDR windows, multi-GPU and RDP need acceptance testing.
- Black-frame detection deliberately rejects completely black/dark images. Partially redacted protected content cannot be reliably identified; never bypass Windows protection.
- The three-second limit applies to frame/GPU-map waits, not every synchronous Windows/driver call. A hung kernel/graphics driver can still delay cleanup; process-level hard isolation/watchdog is not included.
- Snapshot dimensions are bounded to 16384 per side and 32 million pixels; PNG encoding/preview may consume several bounded buffers. No long-lived native capture objects are retained.
- Monitor layout is validated before capture; hardware changes during the frame itself remain a manual retry case.
- MC-009 must pass the meeting session ClockMapping into SnapshotCapture and consume SnapshotResult.Metadata/Png at capture time; the shell currently uses its own monotonic origin. MC-009 supplies context correlation and vision separately.
- MC-012 integrates persistent hotkey settings, unified lifecycle/pause/stop, richer source UX and the preview into the full shell. Existing audio/STT remain unconnected in this foundation shell.
- Release acceptance still requires two physical monitors with mixed scaling, real protected sources, supported app call matrices, and packaging verification. This implementation is not a release-wide compatibility assertion.

## Next physical checks

Place the second monitor left and above primary, use 100% and 150%/200% scaling, refresh sources, select each monitor, and verify crop coordinates/text and overlay absence by visible comparison. Move the fixture between monitors, resize/minimize/close during capture, cancel/retry and exit repeatedly. Test HDR, RDP, screen lock, GPU/device reset and an authorized protected-content source; record OS/app versions, topology and exact failure codes. Keep images in memory unless separately authorized to export synthetic evidence.

## Final local results

- `./scripts/build.ps1`: PASS, Release build, 0 warnings / 0 errors (native snapshot compiler also uses /W4 /WX).
- `./scripts/native-build.ps1`: PASS, accepted audio helper built; native-audio-offline and native-audio-lifetime both passed (2/2).
- `dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore`: exit 0.
- `./scripts/test.ps1`: PASS, 78 tests total: 23 unit, 27 contract, 11 audio, 17 capture; 0 failed, 0 skipped. Offline synthetic playback result PASS.
- Schema export and `git diff --exit-code -- docs/contracts`: exit 0; frozen schemas unchanged.
- `dotnet run --project tests/CaptureWindows -c Release --no-build -- --smoke`: WINDOWS_SMOKE_PASS checks=25 at OS 10.0.26200.0, monitors=1, DPI=120. Actual synthetic pixels captured; no screenshot files written.
