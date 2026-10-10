param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Windows subprocess/dispatcher fixtures have real resource and freshness deadlines.
    # Serialize test projects; individual tests still exercise their intended concurrent pipelines.
    dotnet test MeetingCompanion.slnx -c $Configuration --no-build --no-restore --logger trx --results-directory TestResults -m:1
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
    dotnet run --project tests/PlaybackHarness -c $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Replay failed: $LASTEXITCODE" }
} finally { Pop-Location }
