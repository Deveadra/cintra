$ErrorActionPreference = 'Stop'
[pscustomobject]@{
    OS = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    WindowsSdkInclude = Test-Path 'C:\Program Files (x86)\Windows Kits\10\Include'
}
dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect .NET SDKs.' }
dotnet --list-runtimes
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect .NET runtimes.' }
if (Test-Path 'C:\Program Files (x86)\Windows Kits\10\Include') {
    Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\Include' -Directory | Select-Object Name
}
