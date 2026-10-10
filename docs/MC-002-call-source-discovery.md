# MC-002 Windows source discovery and MC-012 handoff

MC-002 adds a read-only production discovery/selection layer under MeetingCompanion.Platform.
The accepted C++20 WASAPI helper remains the capture implementation. No WPF redesign,
provider request, automatic application launch, meeting join, audio capture or recording is introduced.

Starting main: 432d81c21b7136ecc92d2b942e64ff9e2f83026b (accepted PR #5).
Branch: orion/mc-002-call-source-discovery. Evidence: [MC-002-evidence.md](MC-002-evidence.md).

## Platform APIs

Share one WindowsCallSourceInventory instance among three WindowsCallSourceAdapter instances,
using CallSourceKind.Zoom, Teams and Manual. They implement the frozen ICallSourceAdapter;
no shared DTO, IPC or schema change is required. Fixtures supply ICallSourceInventory.

DiscoverAsync offers candidates. Zoom.exe, ms-teams.exe and Teams.exe are exact executable-name
discovery hints, not authenticated publisher identity or proof of a call. Nested matching app
processes are grouped under the highest observed matching ancestor. A generic WebView renderer
without a verified live calling-app ancestor is available through Manual, never guessed to be Teams.
Manual offers every observed positive PID, including apps with no active audio session; choosing
a root includes its observed descendants and all future audio in that Windows process tree.
Browser hints explicitly warn that other tabs may be included. No calendar is consulted:
meetings, received/outgoing unscheduled calls, minimized apps and idle apps use the same discovery.

Each process is identified by PID, GetProcessTimes creation FILETIME and full executable path.
A parent created after its child is a reused/stale parent edge and is excluded. Unreadable or
changing identities in the selected tree prevent selection. Process snapshots bracket session
enumeration; unstable members are represented with unknown identity, and their sessions are
discarded. Windows snapshots are observations, not an atomic transaction across the whole OS.
Window handles are optional associations; no title or participant/meeting text is read.

Candidates carry deterministic fingerprints of root/member identities and nonexpired session
endpoint/instance IDs. They are held in an adapter-local registry for the latest discovery.
Returned array mutation cannot change the internal offer. Resolve/Probe re-enumerate, reject
unoffered/modified choices, PID reuse, changed tree/renderer/session membership and shared
sessions. Hold/resume activity changes alone preserve identity; session replacement does not.
Multiple roots are separate explicit choices; there is no auto-select-first or follow-new-PID policy.
Shared nonsystem sessions block selection even when their creator is outside the candidate tree:
GetProcessId cannot identify all participants in such a session.

ValidateAsync returns CallSourceStatus: Ready, Unsupported, InventoryUnavailable,
IdentityUnavailable, SourceLost, SourceChanged or Ambiguous, with a diagnostic code and active
renderer PIDs. Ready means eligible for operator selection, not healthy capture or verified call audio.
RenderActive means a WASAPI session stream is running. ProbeAsync never returns AudioDetected
from metadata: it returns Silence with an explicit audio_unverified diagnostic for valid sources.
This is a conservative mapping to the frozen enum; MC-012 must interpret that diagnostic as
"Audio not checked", not as an acoustic measurement or a verified silent call.

AcquireAsync is the capture selection boundary. It validates the current offered candidate and
opens a query/synchronize process handle, verifies creation identity again and returns SelectedCallSource.
EnsureAlive detects exit; Dispose releases the handle. Keep this lease through native startup and Stop.
Holding the process object closes the discovery-to-native numeric-PID reuse hazard; the native helper
then also owns a process handle. ResolveAsync alone returns the frozen numeric-PID DTO and **must
not** be used as a durable selection across asynchronous startup without AcquireAsync's lease.

## MC-012 integration requirements

1. Instantiate inventory/adapters once for the desktop session. Run discovery away from the UI
   thread. Offer Zoom/Teams candidates plus an explicit manual app/process list; show multiple
   roots as separate choices. Retain the last operator choice/input when refreshing or failing.
2. Display user-readable tasks/errors: "Choose the app for incoming audio", "No Zoom process
   found. Open Zoom or choose another app", "Audio sources could not be checked. Try again",
   "This app changed. Choose its audio source again" and "This audio session is shared".
   Put PID/path/tree/session diagnostics behind details. Don't expose account paths in routine logs.
3. Serialize refresh/select/start/pause/stop. Call AcquireAsync for the operator's chosen candidate.
   Immediately before Start, call EnsureAlive; pass Resolved.Source as CaptureSelection.Incoming
   and the independently selected microphone as Microphone. Hold the lease until Stop/Dispose
   finishes, including startup failures. Do not start capture merely because discovery found an app.
4. Poll ValidateAsync for the active offered choice (e.g. once per second) without overlapping
   inventory workers. Do not rediscover/replace the offer while a selection is being committed.
   Tree or renderer changes may occur between snapshots; the poll interval is an uncertainty
   interval, not zero-time detection. Stop capture and discard uncertain queued audio on invalidity,
   mark a typed source-change/audio gap from the last confirmed observation, and request fresh
   selection. Never silently follow a restarted root or recycled PID.
5. Use native CaptureHealth/Gap/SourceChanged and STT health for session state. Listening still
   requires fresh healthy/silent local capture, remote capture, local STT and remote STT.
   Ready/RenderActive/ProbeOutcome.Silence do not satisfy this gate.
6. Preserve the operator's input and selected app label during recovery. After Stop dispose the
   identity lease and audio session; start a fresh session for a freshly confirmed source.
   Treat transfer/rejoin that replaces sessions or renderers as explicit source change.
7. Only claim Zoom/Teams meeting/direct-call compatibility after the real-call matrix passes
   with observable source identity and unrelated-media exclusion. All four modes remain unverified.

## Environment and failure behavior

Windows x64 and process-loopback build >=20348 are checked (Windows 11 is the product target).
.NET 10.0.401, the existing native MSVC/SDK toolchain and the Windows audio service remain required.
COM session inventory uses a dedicated MTA worker and releases all owned RCWs/handles.
Caller waits have a five-second bound and cancellation; only one OS inventory worker can be in
flight per inventory instance. A blocked native OS call itself cannot be forcibly canceled:
later callers share that outstanding operation rather than accumulating workers. Timeout yields
inventory_timeout/ProcessesComplete=false. There is no automatic retry loop or capture fallback.
Process metadata failures are per-process. Audio endpoint/session access failure is incomplete
inventory and blocks selection rather than reporting no source. No active render endpoint also
blocks selection. Existing System output fallback is neither introduced nor silently selected.

Unverified/unsupported: real Zoom/Teams media attribution; browser tab isolation; out-of-tree
render brokers; privileged/protected/exclusive/offload audio; publisher verification; multiple
concurrent calls sharing one app tree; notifications vs remote speech; actual transfer/device
recovery, microphone echo and second hardware configuration. A valid root can render silence or
unrelated notifications. Selection does not promise that any particular remote speaker is heard.

## Reproduction

```powershell
./scripts/build.ps1
dotnet test tests/Platform/MeetingCompanion.Platform.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests/PlatformWindows -c Release --no-build --no-restore
dotnet run --project tests/PlatformWindows -c Release --no-build --no-restore -- --select-pid <observed PID>
./scripts/native-build.ps1
./scripts/test.ps1
dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore
```

The inventory harness defaults to proving a manual selection of its own process, without capture.
It reports OS build, endpoint names, counts, candidate trees, executable versions and active
renderer PIDs. Full executable paths and session instance strings stay out of its output.
A missing selected PID, invalid source or incomplete inventory exits 2; cancellation is reported.
Fixture tests run in the existing standard offline solution/CI, without apps, devices or paid APIs.

## API references

- [GetState](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol-getstate):
  active indicates running streams, not signal/speech identity.
- [GetProcessId](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol2-getprocessid):
  AUDCLNT_S_NO_SINGLE_PROCESS is shared-session ambiguity.
- [Win32_Process](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-process):
  parent IDs can refer to reused processes; compare creation time.
- [Process handles](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers):
  PID/handle lifetime and process identity.
- [PID reuse and held process objects](https://devblogs.microsoft.com/oldnewthing/20110107-00/?p=11803):
  a held process handle retains the process object and its PID through exit.
