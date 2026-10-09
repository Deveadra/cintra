$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project src/MeetingCompanion.Desktop
    if ($LASTEXITCODE -ne 0) { throw "Desktop exited: $LASTEXITCODE" }
} finally { Pop-Location }
