param(
    [string]$OutputDir = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$runId = "m5c-" + (Get-Date -Format "yyyyMMdd-HHmmss")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo "_local_artifacts\release\$runId"
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$logPath = Join-Path $OutputDir "release-sequence.log"
$reportPath = Join-Path $OutputDir "m5c-report.json"
$steps = New-Object System.Collections.Generic.List[object]

function Write-Log([string]$Text) {
    $line = (Get-Date -Format "o") + " " + $Text
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Resolve-Dotnet {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:AHZ_DOTNET)) {
        $candidates.Add($env:AHZ_DOTNET)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidates.Add((Join-Path $env:DOTNET_ROOT "dotnet.exe"))
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) { $candidates.Add($command.Source) }
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or
            -not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($sdks -join ""))) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw ".NET SDK not found. Install .NET 10 SDK or set AHZ_DOTNET."
}

function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    $stepLog = Join-Path $OutputDir ($safeName + ".log")
    $output = & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $stepLog -Encoding UTF8
    $ok = $exitCode -eq 0
    $steps.Add([ordered]@{
        name = $Name
        ok = $ok
        exit_code = $exitCode
        log = [IO.Path]::GetFileName($stepLog)
    })
    foreach ($line in $output) { Write-Log ("  " + $line) }
    Write-Log "CHECK $Name exit=$exitCode ok=$ok"
    return [pscustomobject]@{
        Ok = $ok
        ExitCode = $exitCode
        Text = ($output -join "`n")
    }
}

$dotnet = $null
try {
    Write-Log "START gate=M5-c repo=$repo configuration=$Configuration output=$OutputDir"
    $dotnet = Resolve-Dotnet
    $faceExe = Join-Path $repo ("ahz-scribe-face\src\bin\" + $Configuration +
        "\net10.0-windows\AhzScribe.Face.exe")
    $brainProject = Join-Path $repo "ahz-scribe-brain\tests\AhzScribe.Brain.CoreSmoke.csproj"
    if (-not (Test-Path -LiteralPath $faceExe)) { throw "face executable missing: $faceExe" }
    $env:DEEPSEEK_API_KEY = ""

    $brain = Invoke-Check "brain-core" $dotnet @(
        "run", "--project", $brainProject, "-c", $Configuration, "--no-restore"
    )
    $modelSwitch = Invoke-Check "migration-model-switch" $faceExe @("--model-switch-test")
    $soulImport = Invoke-Check "migration-soul-import" $faceExe @("--soul-migration-test")
    $migrationDiff = Invoke-Check "migration-diff" $faceExe @("--migration-diff-test")
    $faults = Invoke-Check "fault-matrix" $faceExe @("--fault-matrix-test")
    $adaptation = Invoke-Check "experience-adaptation" $faceExe @("--adaptation-test")
    $proactive = Invoke-Check "proactive-gate" $faceExe @("--proactive-test")
    $priority = Invoke-Check "pulse-priority" $faceExe @("--pulse-test")
    $proactivePolicy = Invoke-Check "proactive-memory-policy" $faceExe @("--policy-test")
    $budget = Invoke-Check "resource-budget-ui" $faceExe @("--budget-ui-test")
    $identity = Invoke-Check "identity-invariants" $faceExe @("--identity-ui-test")
    $emergency = Invoke-Check "emergency-stop-resume" $faceExe @("--emergency-stop-ui-test")

    $migrationKinds = @()
    foreach ($kind in @("normal", "model", "dormancy", "device", "soul")) {
        if ($migrationDiff.Text.Contains($kind + "=True")) { $migrationKinds += $kind }
    }
    $migrationRuns = New-Object System.Collections.Generic.List[bool]
    $migrationRuns.Add($modelSwitch.Ok -and $modelSwitch.Text.Contains("MODEL SWITCH: PASS"))
    $migrationRuns.Add($soulImport.Ok -and $soulImport.Text.Contains("SOUL MIGRATION: PASS"))
    $migrationRuns.Add($migrationDiff.Ok -and $migrationDiff.Text.Contains("MIGRATION DIFF: PASS"))
    $migrationPassed = @($migrationRuns | Where-Object { $_ }).Count

    $faultPassed = $faults.Ok -and $faults.Text.Contains("FAULT MATRIX: PASS cases=8/8")
    $actionTraceability = $brain.Ok -and
        $brain.Text.Contains("ACTION OWNERSHIP: PASS") -and
        $brain.Text.Contains("SELF MODEL: PASS provenance=PASS")
    $memoryTraceability = $brain.Ok -and
        $brain.Text.Contains("MMY DUAL WRITE: PASS")
    $rollback = $adaptation.Ok -and
        $adaptation.Text.Contains("ablation_message=20/20") -and
        $adaptation.Text.Contains("preference_ablation_kept=20/20") -and
        $adaptation.Text.Contains("tool_failure_ablation=True")
    $proactiveOk = $proactive.Ok -and
        $proactive.Text.Contains("PROACTIVE GATE: PASS") -and
        $proactivePolicy.Ok -and
        $proactivePolicy.Text.Contains("PROACTIVE POLICY: PASS")
    $priorityOk = $priority.Ok -and
        $priority.Text.Contains("queue_order=user_message,intent_due,body_event,drive,timer,")
    $budgetOk = $budget.Ok -and $budget.Text.Contains("budget_ui=PASS")
    $identityOk = $identity.Ok -and $identity.Text.Contains("identity_ui=PASS")
    $emergencyOk = $emergency.Ok -and
        $emergency.Text.Contains("emergency_stop_ui=PASS") -and
        $emergency.Text.Contains("cancelled=True") -and
        $emergency.Text.Contains("resumed=True")

    $residue = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '^AhzScribe\.(Face|Body|BrainHost)$'
    })
    $noResidue = $residue.Count -eq 0
    $gitHead = (& git -C $repo rev-parse HEAD 2>$null) -join ""
    $gitBranch = (& git -C $repo branch --show-current 2>$null) -join ""
    $gitStatus = @(& git -C $repo status --porcelain 2>$null)

    $pass = $migrationPassed -ge 3 -and $migrationKinds.Count -eq 5 -and
        $faultPassed -and $actionTraceability -and $memoryTraceability -and
        $rollback -and $proactiveOk -and $priorityOk -and $budgetOk -and
        $identityOk -and $emergencyOk -and $noResidue
    $report = [ordered]@{
        schema_version = 1
        gate = "M5-c"
        result = if ($pass) { "M5C_PASS" } else { "FAIL" }
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        git_branch = $gitBranch.Trim()
        git_head = $gitHead.Trim()
        source_clean = $gitStatus.Count -eq 0
        source_changes = $gitStatus.Count
        migrations_run = $migrationPassed
        migration_kinds = $migrationKinds
        fault_cases_passed = if ($faultPassed) { 8 } else { 0 }
        action_traceability = $actionTraceability
        memory_traceability = $memoryTraceability
        behavior_rollback = $rollback
        proactive_policy = $proactiveOk
        task_priority = $priorityOk
        resource_budget = $budgetOk
        identity_invariants = $identityOk
        emergency_stop_resume = $emergencyOk
        residue_processes = $residue.Count
        steps = $steps.ToArray()
        next_gate = "M5-b 168-hour soak"
        release_ready = $false
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Log "RESULT $($report.result) migrations=$migrationPassed kinds=$($migrationKinds -join ',') faults=8/8"
    Write-Log "REPORT $reportPath"
    if (-not $pass) { exit 1 }
    exit 0
}
catch {
    $message = $_.Exception.GetType().FullName + ": " + $_.Exception.Message
    Write-Log "FATAL $message"
    [ordered]@{
        schema_version = 1
        gate = "M5-c"
        result = "FAIL"
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        fatal = $message
        steps = $steps.ToArray()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    exit 1
}
