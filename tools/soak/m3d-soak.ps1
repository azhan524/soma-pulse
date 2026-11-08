param(
    [ValidateSet("M3D", "M5B")]
    [string]$Gate = "M3D",
    [double]$Hours = 0,
    [int]$SampleSeconds = 60,
    [string]$OutputDir = "",
    [switch]$SkipRestartCheck
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ($Hours -le 0) {
    $Hours = if ($Gate -eq "M5B") { 168 } else { 72 }
}
if ($Hours -le 0) { throw "Hours must be greater than zero" }
if ($SampleSeconds -lt 1) { throw "SampleSeconds must be at least 1" }

$existingFace = Get-Process AhzScribe.Face -ErrorAction SilentlyContinue
if ($existingFace) { throw "AhzScribe.Face is already running (PID $($existingFace.Id)); $Gate requires an isolated face process" }

$prefix = if ($Gate -eq "M5B") { "m5b-" } else { "m3d-" }
$runName = $prefix + (Get-Date -Format "yyyyMMdd-HHmmss")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo "_local_artifacts\soak\$runName"
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$faceData = Join-Path $OutputDir "face-data"
$bodyData = Join-Path $OutputDir "body-data"
New-Item -ItemType Directory -Path $faceData -Force | Out-Null
New-Item -ItemType Directory -Path $bodyData -Force | Out-Null

$metricsPath = Join-Path $OutputDir "metrics.jsonl"
$statusPath = Join-Path $OutputDir "status.json"
$summaryPath = Join-Path $OutputDir "summary.json"
$consolePath = Join-Path $OutputDir "soak.log"
$faceLog = Join-Path $OutputDir "face.stdout.log"
$faceErr = Join-Path $OutputDir "face.stderr.log"
$bodyLog = Join-Path $OutputDir "body.stdout.log"
$bodyErr = Join-Path $OutputDir "body.stderr.log"
$pipe = "ahz-m3d-" + [Guid]::NewGuid().ToString("N")
$stopFile = Join-Path $OutputDir "EMERGENCY_STOP"
$requestedDuration = [TimeSpan]::FromHours($Hours)
$startedAt = [DateTimeOffset]::UtcNow
$expectedEnd = $startedAt.Add($requestedDuration)
$runnerPid = $PID

$faceExe = Join-Path $repo "ahz-scribe-face\src\bin\Debug\net10.0-windows\AhzScribe.Face.exe"
$bodyExe = Join-Path $repo "ahz-scribe-body\src\bin\Debug\net10.0-windows\AhzScribe.Body.exe"
if (-not (Test-Path -LiteralPath $faceExe)) { throw "face Debug build missing: $faceExe" }
if (-not (Test-Path -LiteralPath $bodyExe)) { throw "body Debug build missing: $bodyExe" }

$env:AHZ_FACE_DATA = $faceData
$env:AHZ_BODY_DATA = $bodyData
$env:AHZ_BODY_PIPE = $pipe
$env:AHZ_INSTANCE_ID = "soak-" + $pipe
$env:AHZ_STOP_FILE = $stopFile
$env:AHZ_MODEL_BASE = "https://api.deepseek.com"
$env:AHZ_MODEL_NAME = "soak-echo"
Remove-Item Env:DEEPSEEK_API_KEY -ErrorAction SilentlyContinue
$env:AHZ_MODEL_NOPROXY = "1"

function Write-SoakLog([string]$Text) {
    $line = (Get-Date -Format "o") + " " + $Text
    Add-Content -LiteralPath $consolePath -Value $line -Encoding UTF8
    Write-Output $line
}

function Get-DirectoryBytes([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
    try {
        $sum = Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum
        return [long]($sum.Sum)
    }
    catch { return [long]0 }
}

function Get-FileBytes([string]$Path) {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
        return [long](Get-Item -LiteralPath $Path).Length
    }
    catch { return [long]0 }
}

function Read-Json([string]$Path) {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch { return $null }
}

function Get-HealthName($Value) {
    if ($null -eq $Value) { return "missing" }
    if ($Value -is [string] -and $Value -notmatch '^\d+$') { return $Value }
    switch ([int]$Value) {
        0 { return "Healthy" }
        1 { return "Degraded" }
        2 { return "ReadOnly" }
        3 { return "Stopped" }
        default { return "unknown:" + $Value }
    }
}

function Stop-TestProcess($Process) {
    if ($null -eq $Process) { return }
    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            [void]$Process.WaitForExit(5000)
        }
    }
    catch { }
}

function Start-Body {
    $p = Start-Process -FilePath $bodyExe -WorkingDirectory (Split-Path $bodyExe -Parent) `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $bodyLog -RedirectStandardError $bodyErr
    if ($null -eq $p) { throw "failed to start body" }
    return $p
}

function Start-Face {
    $p = Start-Process -FilePath $faceExe -ArgumentList "--silent" `
        -WorkingDirectory (Split-Path $faceExe -Parent) `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $faceLog -RedirectStandardError $faceErr
    if ($null -eq $p) { throw "failed to start face" }
    return $p
}

function Wait-Health([int]$Seconds = 20, [switch]$RequireHealthy) {
    $path = Join-Path $faceData "health.json"
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $health = Read-Json $path
        if ($null -ne $health -and $null -ne $health.State) {
            $health.State = Get-HealthName $health.State
            if (-not $RequireHealthy -or $health.State -eq "Healthy") { return $health }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

$face = $null
$body = $null
$fatal = ""
try {
    Write-SoakLog "START gate=$Gate run=$OutputDir hours=$Hours sample_seconds=$SampleSeconds pipe=$pipe"
    $body = Start-Body
    Start-Sleep -Seconds 1
    $face = Start-Face
    $initialHealth = Wait-Health 30 -RequireHealthy
    if ($null -eq $initialHealth) { throw "face did not produce health.json" }
    Write-SoakLog "READY face_pid=$($face.Id) body_pid=$($body.Id) health=$($initialHealth.State)"
    $initialSoul = ""
    $initialInvariantDigest = ""
    $soulPath = Join-Path $faceData "soul_id"
    $digestPath = Join-Path $faceData "identity-invariants.sha256"
    if (Test-Path -LiteralPath $soulPath) { $initialSoul = (Get-Content -LiteralPath $soulPath -Raw -Encoding UTF8).Trim() }
    if (Test-Path -LiteralPath $digestPath) { $initialInvariantDigest = (Get-Content -LiteralPath $digestPath -Raw -Encoding UTF8).Trim() }
    if ([string]::IsNullOrWhiteSpace($initialSoul) -or [string]::IsNullOrWhiteSpace($initialInvariantDigest)) {
        throw "identity baseline missing before soak"
    }

    $deadline = (Get-Date).Add($requestedDuration)
    $sampleIndex = 0
    $previousSampleAt = $null
    while ((Get-Date) -lt $deadline) {
        $sampleIndex++
        $sampleAt = [DateTimeOffset]::UtcNow
        $sampleGapSeconds = if ($null -eq $previousSampleAt) { 0 } else { [Math]::Round(($sampleAt - $previousSampleAt).TotalSeconds, 3) }
        $previousSampleAt = $sampleAt
        if ($sampleGapSeconds -gt ($SampleSeconds * 3 + 5) -and [string]::IsNullOrEmpty($fatal)) {
            $fatal = "sampling_gap_seconds=$sampleGapSeconds"
        }
        $face.Refresh()
        $body.Refresh()
        $health = Read-Json (Join-Path $faceData "health.json")
        $budget = Read-Json (Join-Path $faceData "resource-budget.json")
        $memoryBytes = 0
        foreach ($name in @("memory.jsonl", "semantic.jsonl", "episodic.jsonl", "disputed.jsonl")) {
            $memoryBytes += Get-FileBytes (Join-Path $faceData $name)
        }
        $memoryBytes += Get-DirectoryBytes (Join-Path $faceData "memory")
        $logBytes = 0
        foreach ($name in @("pulse.jsonl", "sessions.jsonl", "audit.jsonl", "pulse.log")) {
            $logBytes += Get-FileBytes (Join-Path $faceData $name)
        }
        $faceAlive = -not $face.HasExited
        $bodyAlive = -not $body.HasExited
        $currentSoul = ""
        $currentInvariantDigest = ""
        if (Test-Path -LiteralPath $soulPath) { $currentSoul = (Get-Content -LiteralPath $soulPath -Raw -Encoding UTF8).Trim() }
        if (Test-Path -LiteralPath $digestPath) { $currentInvariantDigest = (Get-Content -LiteralPath $digestPath -Raw -Encoding UTF8).Trim() }
        $identityPreserved = $currentSoul -eq $initialSoul -and
            $currentInvariantDigest -eq $initialInvariantDigest
        if (-not $faceAlive -or -not $bodyAlive) {
            $fatal = "process_exited face_alive=$faceAlive body_alive=$bodyAlive"
        }
        if (-not $identityPreserved -and [string]::IsNullOrEmpty($fatal)) {
            $fatal = "identity_changed soul=$($currentSoul -ne $initialSoul) digest=$($currentInvariantDigest -ne $initialInvariantDigest)"
        }
        $row = [ordered]@{
            ts = $sampleAt.ToString("o")
            expected_end = $expectedEnd.ToString("o")
            runner_pid = $runnerPid
            elapsed_minutes = [Math]::Round(([DateTimeOffset]::UtcNow - $startedAt).TotalMinutes, 3)
            sample = $sampleIndex
            sampling_gap_seconds = $sampleGapSeconds
            face_alive = $faceAlive
            body_alive = $bodyAlive
            identity_preserved = $identityPreserved
            face_working_set_mb = if ($faceAlive) { [Math]::Round($face.WorkingSet64 / 1MB, 2) } else { 0 }
            face_private_mb = if ($faceAlive) { [Math]::Round($face.PrivateMemorySize64 / 1MB, 2) } else { 0 }
            face_handles = if ($faceAlive) { $face.HandleCount } else { 0 }
            face_threads = if ($faceAlive) { $face.Threads.Count } else { 0 }
            body_working_set_mb = if ($bodyAlive) { [Math]::Round($body.WorkingSet64 / 1MB, 2) } else { 0 }
            face_data_mb = [Math]::Round((Get-DirectoryBytes $faceData) / 1MB, 2)
            memory_mb = [Math]::Round($memoryBytes / 1MB, 2)
            logs_mb = [Math]::Round($logBytes / 1MB, 2)
            health = if ($null -eq $health) { "missing" } else { Get-HealthName $health.State }
            active_tasks = if ($null -eq $budget) { -1 } else { [int]$budget.ActiveTasks }
            pending_tasks = if ($null -eq $budget) { -1 } else { [int]$budget.CurrentPendingTasks }
            tool_calls_1h = if ($null -eq $budget) { -1 } else { [int]$budget.ToolCallsLastHour }
            retries_1h = if ($null -eq $budget) { -1 } else { [int]$budget.RetriesLastHour }
            fatal = $fatal
        }
        Add-Content -LiteralPath $metricsPath -Value ($row | ConvertTo-Json -Compress) -Encoding UTF8
        $row | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
        if ($fatal) { break }
        Start-Sleep -Seconds $SampleSeconds
    }
}
catch {
    $fatal = "exception:" + $_.Exception.Message
    Write-SoakLog "FATAL $fatal"
}
finally {
    Stop-TestProcess $face
    Stop-TestProcess $body
}

$rows = @()
if (Test-Path -LiteralPath $metricsPath) {
    foreach ($line in Get-Content -LiteralPath $metricsPath -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $rows += $line | ConvertFrom-Json } catch { }
    }
}

$restartOk = $false
$restartDetail = "skipped"
if (-not $SkipRestartCheck -and [string]::IsNullOrEmpty($fatal) -and $rows.Count -gt 0) {
    try {
        Write-SoakLog "RESTART CHECK"
        $body = Start-Body
        Start-Sleep -Seconds 1
        $face = Start-Face
        $restartHealth = Wait-Health 30 -RequireHealthy
        $face.Refresh()
        $body.Refresh()
        $restartOk = $null -ne $restartHealth -and -not $face.HasExited -and -not $body.HasExited
        $restartDetail = "face_alive=$(-not $face.HasExited) body_alive=$(-not $body.HasExited) health=" +
            $(if ($null -eq $restartHealth) { "missing" } else { $restartHealth.State })
        Write-SoakLog "RESTART RESULT $restartDetail"
    }
    catch {
        $restartDetail = "exception:" + $_.Exception.Message
        $restartOk = $false
        Write-SoakLog "RESTART FAIL $restartDetail"
    }
    finally {
        Stop-TestProcess $face
        Stop-TestProcess $body
    }
}

$durationHours = ([DateTimeOffset]::UtcNow - $startedAt).TotalHours
$maxQueue = 0
$maxLogsMb = 0
$maxMemoryMb = 0
$maxHandles = 0
$maxThreads = 0
$maxSampleGapSeconds = 0
$healthySamples = 0
$healthSamples = 0
$identitySamples = 0
foreach ($row in $rows) {
    if ($null -ne $row.pending_tasks) { $maxQueue = [Math]::Max($maxQueue, [int]$row.pending_tasks) }
    if ($null -ne $row.logs_mb) { $maxLogsMb = [Math]::Max($maxLogsMb, [double]$row.logs_mb) }
    if ($null -ne $row.face_working_set_mb) { $maxMemoryMb = [Math]::Max($maxMemoryMb, [double]$row.face_working_set_mb) }
    if ($null -ne $row.face_handles) { $maxHandles = [Math]::Max($maxHandles, [int]$row.face_handles) }
    if ($null -ne $row.face_threads) { $maxThreads = [Math]::Max($maxThreads, [int]$row.face_threads) }
    if ($null -ne $row.sampling_gap_seconds) { $maxSampleGapSeconds = [Math]::Max($maxSampleGapSeconds, [double]$row.sampling_gap_seconds) }
    if ($row.health -ne "missing") {
        $healthSamples++
        if ($row.health -eq "Healthy") { $healthySamples++ }
    }
    if ($row.identity_preserved -eq $true) { $identitySamples++ }
}
$baseline = if ($rows.Count -gt 0) { [double]$rows[0].face_working_set_mb } else { 0 }
$finalMemory = if ($rows.Count -gt 0) { [double]$rows[$rows.Count - 1].face_working_set_mb } else { 0 }
$memoryGrowth = $finalMemory - $baseline
$healthyRatio = if ($healthSamples -gt 0) { $healthySamples / $healthSamples } else { 0 }
$requiredHours = if ($Gate -eq "M5B") { 167.9 } else { 71.9 }
$qualifies = $durationHours -ge $requiredHours
$identityPreserved = $rows.Count -gt 0 -and $identitySamples -eq $rows.Count
$runtimeOk = $rows.Count -ge 2 -and [string]::IsNullOrEmpty($fatal) -and $maxQueue -le 8 -and
    $maxLogsMb -le 32 -and $memoryGrowth -le 512 -and $healthyRatio -ge 0.95 -and
    $maxSampleGapSeconds -le ($SampleSeconds * 3 + 5) -and $restartOk -and $identityPreserved
$passResult = if ($Gate -eq "M5B") { "M5B_PASS" } else { "M3D_PASS" }
$result = if ($runtimeOk -and $qualifies) { $passResult } elseif ($runtimeOk) { "SMOKE_PASS" } else { "FAIL" }
$summary = [ordered]@{
    result = $result
    gate = $Gate
    qualifies_for_gate = $qualifies
    qualifies_for_m3d = if ($Gate -eq "M3D") { $qualifies } else { $false }
    requested_hours = $Hours
    duration_hours = [Math]::Round($durationHours, 4)
    samples = $rows.Count
    fatal = $fatal
    restart_ok = $restartOk
    restart_detail = $restartDetail
    identity_preserved = $identityPreserved
    identity_samples = $identitySamples
    initial_soul_id = $initialSoul
    initial_invariant_digest = $initialInvariantDigest
    max_pending_tasks = $maxQueue
    max_logs_mb = [Math]::Round($maxLogsMb, 2)
    baseline_face_working_set_mb = [Math]::Round($baseline, 2)
    final_face_working_set_mb = [Math]::Round($finalMemory, 2)
    memory_growth_mb = [Math]::Round($memoryGrowth, 2)
    max_face_working_set_mb = [Math]::Round($maxMemoryMb, 2)
    max_face_handles = $maxHandles
    max_face_threads = $maxThreads
    max_sample_gap_seconds = [Math]::Round($maxSampleGapSeconds, 3)
    healthy_ratio = [Math]::Round($healthyRatio, 4)
    output_dir = $OutputDir
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-SoakLog ("RESULT " + $result + " duration_hours=" + $summary.duration_hours +
    " samples=" + $summary.samples + " memory_growth_mb=" + $summary.memory_growth_mb +
    " max_pending=" + $summary.max_pending_tasks + " max_logs_mb=" + $summary.max_logs_mb +
    " restart=" + $restartOk)

if ($result -eq "FAIL") { exit 1 }
exit 0
