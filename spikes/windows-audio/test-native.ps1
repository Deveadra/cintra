param(
    [switch]$AcknowledgePlayback,
    [switch]$IncludeMicrophone
)
$ErrorActionPreference = 'Stop'
if (-not $AcknowledgePlayback) { throw 'This test plays bounded low-amplitude synthetic tones. Pass -AcknowledgePlayback. -IncludeMicrophone additionally meters microphone 0 for 3 seconds.' }
$probeExe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\WindowsAudioSpike.exe'
if (-not (Test-Path -LiteralPath $probeExe)) { throw 'Run build.ps1 first.' }
$logDirectory = Join-Path $PSScriptRoot 'bin\native-test-logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Start-Fixture([string]$Name, [string[]]$Arguments) {
    Start-Process -FilePath $probeExe -ArgumentList $Arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logDirectory "$Name.jsonl") `
        -RedirectStandardError (Join-Path $logDirectory "$Name.stderr")
}
function Finish-Fixture($Process, [string]$Name, [int]$ExpectedExit = 0) {
    if (-not $Process.WaitForExit(12000)) {
        # Only kill the specific child started by this script, never a selected external app.
        $Process.Kill()
        $Process.WaitForExit()
        throw "$Name exceeded watchdog deadline"
    }
    $Process.Refresh()
    if ($Process.ExitCode -ne $ExpectedExit) { throw "$Name exit $($Process.ExitCode), expected $ExpectedExit; see $logDirectory" }
    $rows = @(Get-Content -LiteralPath (Join-Path $logDirectory "$Name.jsonl") | ForEach-Object { $_ | ConvertFrom-Json })
    $Process.Dispose()
    return $rows
}
function Run-Probe([string]$Name, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $child = Start-Fixture $Name $Arguments
    Finish-Fixture $child $Name $ExpectedExit
}
function Wait-ToneReady($Process, [string]$Name) {
    $deadline = [System.Diagnostics.Stopwatch]::StartNew()
    $path = Join-Path $logDirectory "$Name.jsonl"
    while ($deadline.Elapsed.TotalSeconds -lt 6) {
        if ($Process.HasExited) { throw "$Name exited before playback readiness" }
        if ((Test-Path -LiteralPath $path) -and (Select-String -LiteralPath $path -Pattern '"kind":"tone_ready"' -Quiet)) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "$Name did not become ready within 6 seconds"
}

$tone = Start-Fixture 'tone' @('tone','--seconds','10','--acknowledge-playback')
try {
    Wait-ToneReady $tone 'tone'
    $included = @(Run-Probe 'included' @('capture','--pid',"$($tone.Id)",'--seconds','3','--acknowledge-capture'))
    if ($tone.HasExited) { throw 'Tone exited before exclusion test; no valid concurrent-audio evidence.' }
    $excluded = @(Run-Probe 'excluded' @('capture','--pid','self','--seconds','3','--acknowledge-capture'))
    if ($tone.HasExited) { throw 'Tone exited during exclusion test; result inconclusive.' }
    $toneRows = @(Finish-Fixture $tone 'tone')
    $includedPeak = ($included | Where-Object kind -eq 'stopped').stats.peak
    $excludedPeak = ($excluded | Where-Object kind -eq 'stopped').stats.peak
    if ($includedPeak -lt 0.005 -or $excludedPeak -gt 0.0001) { throw "Isolation criterion failed: included=$includedPeak excluded=$excludedPeak" }
    [pscustomobject]@{ test='synthetic_process_isolation'; includedPeak=$includedPeak; excludedPeak=$excludedPeak; result='PASS'; callCompatibility='NOT_VERIFIED' } | ConvertTo-Json -Compress
}
finally {
    # Process object may already be disposed after normal completion.
    try { if (-not $tone.HasExited) { $tone.Kill(); $tone.WaitForExit() } } catch [System.InvalidOperationException] { }
    $tone.Dispose()
}
$shortTone = Start-Fixture 'short-tone' @('tone','--seconds','2','--acknowledge-playback')
Wait-ToneReady $shortTone 'short-tone'
$exitRows = @(Run-Probe 'source-exit' @('capture','--pid',"$($shortTone.Id)",'--seconds','5','--acknowledge-capture') 2)
$null = Finish-Fixture $shortTone 'short-tone'
if (-not ($exitRows | Where-Object { $_.kind -eq 'capture_error' -and $_.detail -like '*source_exited*' })) { throw 'Expected explicit source_exited diagnostic.' }
[pscustomobject]@{test='source_exit';result='PASS'} | ConvertTo-Json -Compress
$null = Run-Probe 'invalid-mic' @('capture','--mic','4294967295','--seconds','1','--acknowledge-capture') 2
foreach ($cycle in 1..3) {
    $null = Run-Probe "cycle-$cycle" @('capture','--pid','self','--seconds','1','--acknowledge-capture')
}
[pscustomobject]@{test='invalid_mic_and_three_fresh_capture_cycles';result='PASS';handleLeakProof=$false} | ConvertTo-Json -Compress
if ($IncludeMicrophone) {
    $micRows = @(Run-Probe 'microphone' @('capture','--mic','0','--seconds','3','--acknowledge-capture'))
    $summary = $micRows | Where-Object kind -eq 'stopped'
    if ($summary.stats.packets -lt 1) { throw 'No microphone packets.' }
    [pscustomobject]@{test='microphone_packets';result='PASS';stats=$summary.stats;speakerIdentity='NOT_VERIFIED'} | ConvertTo-Json -Depth 5 -Compress
}
