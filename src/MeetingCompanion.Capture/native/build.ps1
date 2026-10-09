$ErrorActionPreference = 'Stop'
$snapshotRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$snapshotOutput = Join-Path $snapshotRoot 'artifacts/snapshot'
New-Item -ItemType Directory -Force -Path $snapshotOutput | Out-Null
$snapshotVs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$snapshotVs) { throw 'MC-008 requires MSVC x64 and Windows SDK C++/WinRT headers.' }
$snapshotDev = Join-Path $snapshotVs 'Common7/Tools/VsDevCmd.bat'
$snapshotSource = Join-Path $PSScriptRoot 'capture.cpp'
$snapshotDll = Join-Path $snapshotOutput 'Cintra.Snapshot.dll'
if ((Test-Path $snapshotDll) -and (Get-Item $snapshotDll).LastWriteTimeUtc -gt (Get-Item $snapshotSource).LastWriteTimeUtc -and (Get-Item $snapshotDll).LastWriteTimeUtc -gt (Get-Item $PSCommandPath).LastWriteTimeUtc) { exit 0 }
Push-Location $snapshotOutput
try {
    & cmd /d /s /c "`"$snapshotDev`" -arch=x64 -host_arch=x64 && cl /nologo /std:c++20 /EHsc /W4 /WX /O2 /MD /LD `"$snapshotSource`" /Fe:`"$snapshotDll`" /link windowsapp.lib d3d11.lib dxgi.lib user32.lib ole32.lib"
    if ($LASTEXITCODE -ne 0) { throw 'MC-008 native WGC build failed.' }
} finally { Pop-Location }
