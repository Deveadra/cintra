$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$vswherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$buildTools = & $vswherePath -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$buildTools) { throw 'Native MSVC x64 tools unavailable.' }
$devcmd = Join-Path $buildTools 'Common7\Tools\VsDevCmd.bat'
Push-Location $repoRoot
try {
    & cmd /d /s /c "`"$devcmd`" -arch=x64 -host_arch=x64 && cmake -S src/MeetingCompanion.Native -B artifacts/native -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_TRY_COMPILE_CONFIGURATION=Release -DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded && cmake --build artifacts/native && ctest --test-dir artifacts/native --output-on-failure"
    if ($LASTEXITCODE -ne 0) { throw 'Native build/test failed.' }
} finally { Pop-Location }
