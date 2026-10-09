# Building the MC-001 foundation

The original README and implementation pack remain unchanged as historical handoff inputs.
The executable foundation is now in `MeetingCompanion.slnx`; this is not a functioning meeting assistant yet.

Prerequisites: Windows x64 and .NET SDK 10.0.401 (patch roll-forward allowed by global.json).
The managed WPF build uses the installed Windows Desktop targeting pack; native Windows SDK/C++ tools
are not needed until the audio helper is integrated. Initial test-package restore needs nuget.org;
test execution itself is offline and needs no API credentials, audio hardware, or meeting applications.

From the repository root in PowerShell:

```powershell
./scripts/check-environment.ps1
./scripts/build.ps1
dotnet format MeetingCompanion.slnx --verify-no-changes --no-restore
./scripts/test.ps1
./scripts/run-dev.ps1
```

The desktop shell states that capture is not connected. It makes no provider calls or capture requests.
Test scripts fail on native command errors. CI builds WPF and all managed boundaries on Windows,
runs xUnit and deterministic synthetic replay, checks formatting and schema drift, and uploads TRX.
CI execution and real Windows audio compatibility require separate evidence.

Regenerate reviewed schemas after contract edits:

```powershell
dotnet run --project tests/PlaybackHarness -c Release -- --export-schemas docs/contracts
```

Use `docs/contracts/README.md` as the audio-agent integration handoff, and `docs/ADR/0001-foundation.md`
for ownership, dependency, compatibility and lifecycle decisions.
