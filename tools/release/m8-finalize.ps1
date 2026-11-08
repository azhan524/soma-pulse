param(
    [string]$OutputDir = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo ("_local_artifacts\release\m8-preflight-" +
        (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$logPath = Join-Path $OutputDir "m8-preflight.log"
$reportPath = Join-Path $OutputDir "m8-preflight.json"

function Write-Log([string]$Text) {
    $line = (Get-Date -Format "o") + " " + $Text
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Run-Script([string]$Name, [string]$Path, [string[]]$Arguments) {
    $stepLog = Join-Path $OutputDir ($Name + ".log")
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments 2>&1 |
        ForEach-Object { $_.ToString() }
    $exit = $LASTEXITCODE
    $output | Set-Content -LiteralPath $stepLog -Encoding UTF8
    foreach ($line in $output) { Write-Log ("  " + $line) }
    Write-Log "STEP $Name exit=$exit"
    return [pscustomobject]@{ Ok = $exit -eq 0; ExitCode = $exit; Text = ($output -join "`n") }
}

function Run-Executable([string]$Name, [string]$Path, [string[]]$Arguments) {
    $stepLog = Join-Path $OutputDir ($Name + ".log")
    $output = & $Path @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exit = $LASTEXITCODE
    $output | Set-Content -LiteralPath $stepLog -Encoding UTF8
    foreach ($line in $output) { Write-Log ("  " + $line) }
    Write-Log "STEP $Name exit=$exit"
    return [pscustomobject]@{ Ok = $exit -eq 0; ExitCode = $exit; Text = ($output -join "`n") }
}

try {
    if ([string]::IsNullOrWhiteSpace($env:AHZ_DOTNET)) {
        throw "AHZ_DOTNET is required for M8 preflight"
    }
    Write-Log "START gate=M8-b-preflight repo=$repo output=$OutputDir"

    $requiredDocs = @(
        "m1-a-self-model.md", "m1-b-claim-policy.md", "m1-c-claim-ingestion.md",
        "m2-a-experience-change.md", "m2-b-evidence-recovery.md",
        "m2-c-tool-failure.md", "m2-d-preference-correction.md",
        "m3-a-health-manager.md", "m3-b-resource-budget.md",
        "m3-c-fault-matrix.md", "m3-d-soak.md",
        "m4-a-identity-manifest.md", "m4-b-model-switch.md",
        "m4-c-new-device-import.md", "m4-d-migration-diff.md",
        "m5-a-release-candidate.md", "m5-b-168-hour-soak.md",
        "m5-c-release-sequence.md",
        "m6-a-autobiography.md", "m6-b-attention.md", "m6-c-memory-lifecycle.md",
        "m7-bounded-goals.md", "m8-a-safe-self-evolution.md"
    )
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($name in $requiredDocs) {
        $path = Join-Path $repo (Join-Path "docs\acceptance" $name)
        if (-not (Test-Path -LiteralPath $path)) { $missing.Add($name); continue }
        if ($name -eq "m5-b-168-hour-soak.md") {
            $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
            if (-not $text.Contains("DEFERRED")) { $missing.Add($name + ":not_deferred") }
            continue
        }
        if (-not (Get-Content -LiteralPath $path -Raw -Encoding UTF8).Contains("结果：PASS")) {
            $missing.Add($name + ":not_pass")
        }
    }
    $docsOk = $missing.Count -eq 0
    Write-Log "ACCEPTANCE DOCS ok=$docsOk required=$($requiredDocs.Count) missing=$($missing -join ',')"
    if (-not $docsOk) { throw "acceptance matrix incomplete" }

    $verifyDir = Join-Path $OutputDir "release-verify"
    $verify = Run-Script "release-verify" (Join-Path $repo "tools\release\m5-release-verify.ps1") @(
        "-OutputDir", $verifyDir, "-Configuration", $Configuration
    )
    if (-not $verify.Ok) { throw "release verify failed" }
    $verifyReport = Get-Content -LiteralPath (Join-Path $verifyDir "release-report.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($verifyReport.result -ne "M5A_PASS") { throw "M5-a report is not PASS" }

    $sequenceDir = Join-Path $OutputDir "release-sequence"
    $sequence = Run-Script "release-sequence" (Join-Path $repo "tools\release\m5-release-sequence.ps1") @(
        "-OutputDir", $sequenceDir, "-Configuration", $Configuration
    )
    if (-not $sequence.Ok) { throw "release sequence failed" }
    $sequenceReport = Get-Content -LiteralPath (Join-Path $sequenceDir "m5c-report.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($sequenceReport.result -ne "M5C_PASS") { throw "M5-c report is not PASS" }

    $bodyExe = Join-Path $repo ("ahz-scribe-body\src\bin\" + $Configuration +
        "\net10.0-windows\AhzScribe.Body.exe")
    $bodySelf = Run-Executable "body-self" $bodyExe @("--self-test")
    $bodyIpc = Run-Executable "body-ipc" $bodyExe @("--ipc-test")
    if (-not $bodySelf.Ok -or -not $bodyIpc.Ok) { throw "body verification failed" }

    $soulLog = Join-Path $verifyDir "face-soul-migration-test.log"
    $cleanRestore = (Test-Path -LiteralPath $soulLog) -and
        (Get-Content -LiteralPath $soulLog -Raw -Encoding UTF8).Contains("SOUL MIGRATION: PASS")
    $evolutionOk = $bodySelf.Text.Contains("SAFE SELF-EVOLUTION: PASS")
    $blocking = @((Get-Content -LiteralPath (Join-Path $repo "docs\release\issue-register.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json).issues | Where-Object {
            $_.status -eq "open" -and ($_.severity -eq "P0" -or $_.severity -eq "P1")
        })
    $residue = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '^AhzScribe\.(Face|Body|BrainHost)$'
    })
    $gitHead = ((& git -C $repo rev-parse HEAD 2>$null) -join "").Trim()
    $gitStatus = @(& git -C $repo status --porcelain 2>$null)

    $preflightPass = $docsOk -and $verify.Ok -and $sequence.Ok -and $bodySelf.Ok -and
        $bodyIpc.Ok -and $cleanRestore -and $evolutionOk -and $blocking.Count -eq 0 -and
        $residue.Count -eq 0
    $report = [ordered]@{
        schema_version = 1
        gate = "M8-b-preflight"
        result = if ($preflightPass) { "M8B_PRECHECK_PASS" } else { "FAIL" }
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        acceptance_docs = $requiredDocs.Count
        clean_target_soul_restore = $cleanRestore
        safe_self_evolution = $evolutionOk
        body_self_test = $bodySelf.Ok
        body_ipc = $bodyIpc.Ok
        release_verify = $verifyReport.result
        release_sequence = $sequenceReport.result
        face_modes = "$($verifyReport.face_modes_passed)/$($verifyReport.face_modes_total)"
        open_p0_p1 = $blocking.Count
        residue_processes = $residue.Count
        git_head = $gitHead
        source_clean = $gitStatus.Count -eq 0
        source_changes = $gitStatus.Count
        pending_final_soak_hours = 168
        release_ready = $false
        next_gate = "Final 168-hour soak, then M8 final declaration"
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Log "RESULT $($report.result) clean_restore=$cleanRestore self_evolution=$evolutionOk face=$($report.face_modes)"
    Write-Log "REPORT $reportPath"
    if (-not $preflightPass) { exit 1 }
    exit 0
}
catch {
    $message = $_.Exception.GetType().FullName + ": " + $_.Exception.Message
    Write-Log "FATAL $message"
    [ordered]@{
        schema_version = 1
        gate = "M8-b-preflight"
        result = "FAIL"
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        fatal = $message
    } | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
    exit 1
}
