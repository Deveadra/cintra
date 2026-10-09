param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet test MeetingCompanion.slnx -c $Configuration --no-build --no-restore --logger trx --results-directory TestResults
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
    dotnet run --project tests/PlaybackHarness -c $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Replay failed: $LASTEXITCODE" }
} finally { Pop-Location }
