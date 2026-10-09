param(
    [switch]$AcknowledgeCapture,
    [switch]$AcknowledgePlayback,
    [Parameter(Mandatory)][string]$MicrophoneEndpointId,
    [ValidateRange(1,100)][int]$Cycles = 3
)
$ErrorActionPreference = 'Stop'
if (!$AcknowledgeCapture -or !$AcknowledgePlayback) { throw 'Explicit capture and low-volume tone playback acknowledgements required.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$nativeHelper = Join-Path $repoRoot 'artifacts\native\Cintra.Audio.Native.exe'
$tonePath = Join-Path $repoRoot 'artifacts\native\Cintra.Audio.Tone.exe'
$harnessPath = Join-Path $repoRoot 'tests\AudioHarness\bin\Release\net10.0-windows\Cintra.Audio.Harness.exe'
$logRoot = Join-Path $repoRoot 'artifacts\native-evidence'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
function Start-Fixture([int]$Seconds = 30) {
    $start = [Diagnostics.ProcessStartInfo]::new($tonePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--acknowledge-playback','--seconds',"$Seconds")) { $start.ArgumentList.Add($argument) }
    $fixture = [Diagnostics.Process]::Start($start)
    $ready = $fixture.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    if ($ready -notmatch '^tone_ready') { throw "Renderer not ready: $ready" }
    return $fixture
}
function Capture-Run([string]$TargetPid, [int]$Seconds, [string]$Name, [bool]$ExpectSuccess = $true) {
    $start = [Diagnostics.ProcessStartInfo]::new($harnessPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($nativeHelper,'--capture','--mic',$MicrophoneEndpointId,'--pid',"$TargetPid",'--seconds',"$Seconds",'--acknowledge-capture')) { $start.ArgumentList.Add($argument) }
    if ($Name -eq 'selected-renderer') { $start.ArgumentList.Add('--exercise-controls') }
    $capture = [Diagnostics.Process]::Start($start)
    $output = $capture.StandardOutput.ReadToEndAsync()
    $errors = $capture.StandardError.ReadToEndAsync()
    try {
        if (!$capture.WaitForExit(12000)) { $capture.Kill($true); throw "Capture watchdog timeout: $Name" }
        $lines = $output.GetAwaiter().GetResult()
        $lines | Set-Content -LiteralPath (Join-Path $logRoot "$Name.jsonl")
        $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $logRoot "$Name.stderr.txt")
        if ($ExpectSuccess -and $capture.ExitCode -ne 0) { throw "Capture failed: $Name, exit $($capture.ExitCode). Inspect artifacts/native-evidence." }
        return ($lines -split '\r?\n' | Where-Object { $_ -match '^\{"result"' } | Select-Object -Last 1 | ConvertFrom-Json)
    } finally { $capture.Dispose() }
}
$renderer = Start-Fixture
try {
    $selected = Capture-Run $renderer.Id 3 'selected-renderer'
    if ($selected.frames.LocalMic -le 0 -or $selected.frames.RemoteApp -le 0 -or $selected.peaks.RemoteApp -lt 0.005) { throw 'Selected renderer or independent microphone did not produce expected frames.' }
    if ($renderer.HasExited) { throw 'Fixture exited before exclusion test.' }
    $excluded = Capture-Run 'self' 3 'excluded-renderer'
    if ($renderer.HasExited -or $excluded.peaks.RemoteApp -gt 0.0001 -or $excluded.frames.RemoteApp -le 0) { throw 'Unrelated renderer exclusion failed.' }
} finally {
    if (!$renderer.HasExited) { $renderer.Kill($false); $renderer.WaitForExit() }
    $renderer.Dispose()
}
for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    $result = Capture-Run 'self' 1 "lifecycle-$cycle"
    if ($result.frames.LocalMic -le 0 -or $result.frames.RemoteApp -le 0) { throw "Missing independent frames in cycle $cycle." }
}
$exiting = Start-Fixture 3
try {
    $sourceLost = Capture-Run $exiting.Id 4 'source-termination' $false
    $exitLog = Get-Content -LiteralPath (Join-Path $logRoot 'source-termination.jsonl') -Raw
    if ($sourceLost.result -ne 'FAIL' -or $exitLog -notmatch 'source_terminated' -or $exitLog -notmatch '"reason":"source_changed"') { throw 'Source termination was not detected with an explicit gap.' }
} finally {
    if (!$exiting.HasExited) { $exiting.Kill($false); $exiting.WaitForExit() }
    $exiting.Dispose()
}
$remaining = Get-Process -Name 'Cintra.Audio.Native' -ErrorAction SilentlyContinue
if ($remaining) { throw 'Native capture helper remains after Stop.' }
Write-Output "PASS real native dual-stream capture; selected/excluded renderer; Pause/Resume; source termination gap; $Cycles start/stop cycles; no capture helper remains. Metadata only: $logRoot"
