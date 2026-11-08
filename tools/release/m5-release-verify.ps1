param(
    [string]$OutputDir = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipUi
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$runId = "m5a-" + (Get-Date -Format "yyyyMMdd-HHmmss")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo "_local_artifacts\release\$runId"
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$logPath = Join-Path $OutputDir "release-verify.log"
$reportPath = Join-Path $OutputDir "release-report.json"
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
    if ($null -ne $command) {
        $candidates.Add($command.Source)
    }
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or
            -not (Test-Path -LiteralPath $candidate)) {
            continue
        }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($sdks -join ""))) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw ".NET SDK not found. Install .NET 10 SDK or set AHZ_DOTNET to dotnet.exe."
}

function Add-Step([string]$Name, [bool]$Ok, [int]$ExitCode, [string]$LogFile) {
    $steps.Add([ordered]@{
        name = $Name
        ok = $Ok
        exit_code = $ExitCode
        log = if ($LogFile) { [IO.Path]::GetFileName($LogFile) } else { "" }
    })
}

function Invoke-Captured([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    $stepLog = Join-Path $OutputDir ($safeName + ".log")
    Write-Log "RUN $Name"
    $output = & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $stepLog -Encoding UTF8
    $ok = $exitCode -eq 0
    Add-Step $Name $ok $exitCode $stepLog
    foreach ($line in $output) { Write-Log ("  " + $line) }
    Write-Log "STEP $Name exit=$exitCode ok=$ok"
    return [bool]$ok
}

$dotnet = $null
$faceModes = @(
    "--proactive-test",
    "--body-test",
    "--policy-test",
    "--drive-test",
    "--pulse-test",
    "--state-test",
    "--session-test",
    "--soul-test",
    "--adaptation-test",
    "--fault-matrix-test",
    "--model-switch-test",
    "--soul-migration-test",
    "--migration-diff-test",
    "--preference-ui-test",
    "--health-ui-test",
    "--health-fault-test",
    "--budget-ui-test",
    "--identity-ui-test",
    "--model-switch-ui-test",
    "--migration-diff-ui-test",
    "--autobiography-ui-test",
    "--attention-ui-test",
    "--memory-lifecycle-ui-test",
    "--goal-ui-test",
    "--emergency-stop-ui-test",
    "--schema-test",
    "--sensor-stale-test",
    "--audit-fallback-test"
)

try {
    Write-Log "START repo=$repo configuration=$Configuration output=$OutputDir"
    $dotnet = Resolve-Dotnet
    $sdkList = (& $dotnet --list-sdks) -join "; "
    Write-Log "DOTNET=$dotnet SDK=$sdkList"

    $requiredDocs = @(
        "m1-a-self-model.md", "m1-b-claim-policy.md", "m1-c-claim-ingestion.md",
        "m2-a-experience-change.md", "m2-b-evidence-recovery.md",
        "m2-c-tool-failure.md", "m2-d-preference-correction.md",
        "m3-a-health-manager.md", "m3-b-resource-budget.md",
        "m3-c-fault-matrix.md", "m3-d-soak.md",
        "m4-a-identity-manifest.md", "m4-b-model-switch.md",
        "m4-c-new-device-import.md", "m4-d-migration-diff.md"
    )
    $missingDocs = New-Object System.Collections.Generic.List[string]
    foreach ($name in $requiredDocs) {
        $path = Join-Path $repo (Join-Path "docs\acceptance" $name)
        if (-not (Test-Path -LiteralPath $path) -or
            -not (Get-Content -LiteralPath $path -Raw -Encoding UTF8).Contains("结果：PASS")) {
            $missingDocs.Add($name)
        }
    }
    $docsOk = $missingDocs.Count -eq 0
    Add-Step "acceptance-docs" $docsOk $(if ($docsOk) { 0 } else { 1 }) ""
    Write-Log "ACCEPTANCE DOCS ok=$docsOk missing=$(($missingDocs -join ','))"

    $issuePath = Join-Path $repo "docs\release\issue-register.json"
    if (-not (Test-Path -LiteralPath $issuePath)) {
        throw "issue register missing: $issuePath"
    }
    $issueRegister = Get-Content -LiteralPath $issuePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $openBlocking = @($issueRegister.issues | Where-Object {
        $_.status -eq "open" -and ($_.severity -eq "P0" -or $_.severity -eq "P1")
    })
    $issuesOk = $openBlocking.Count -eq 0
    Add-Step "blocking-issues" $issuesOk $(if ($issuesOk) { 0 } else { 1 }) ""
    Write-Log "BLOCKING ISSUES ok=$issuesOk open_p0_p1=$($openBlocking.Count)"

    $projects = @(
        "contracts\AhzScribe.Contracts.csproj",
        "ahz-scribe-brain\src\AhzScribe.Brain.csproj",
        "ahz-scribe-body\src\AhzScribe.Body.csproj",
        "ahz-scribe-brain-host\src\AhzScribe.BrainHost.csproj",
        "ahz-scribe-face\src\AhzScribe.Face.csproj"
    )
    $buildsOk = $true
    foreach ($project in $projects) {
        $name = "build-" + [IO.Path]::GetFileNameWithoutExtension($project)
        $arguments = @("build", (Join-Path $repo $project), "-c", $Configuration,
            "--nologo", "-v:minimal")
        if (-not (Invoke-Captured $name $dotnet $arguments)) {
            $buildsOk = $false
        }
    }
    if (-not $buildsOk) { throw "one or more builds failed" }

    $brainProject = Join-Path $repo "ahz-scribe-brain\tests\AhzScribe.Brain.CoreSmoke.csproj"
    $brainOk = Invoke-Captured "brain-core" $dotnet @(
        "run", "--project", $brainProject, "-c", $Configuration, "--no-restore"
    )
    if (-not $brainOk) { throw "brain core smoke failed" }

    $framework = if ($Configuration -eq "Release") { "net10.0-windows" } else { "net10.0-windows" }
    $bodyExe = Join-Path $repo ("ahz-scribe-body\src\bin\" + $Configuration + "\" + $framework + "\AhzScribe.Body.exe")
    $faceExe = Join-Path $repo ("ahz-scribe-face\src\bin\" + $Configuration + "\" + $framework + "\AhzScribe.Face.exe")
    if (-not (Test-Path -LiteralPath $bodyExe)) { throw "body executable missing: $bodyExe" }
    if (-not (Test-Path -LiteralPath $faceExe)) { throw "face executable missing: $faceExe" }

    $bodySelfOk = Invoke-Captured "body-self-test" $bodyExe @("--self-test")
    $bodyIpcOk = Invoke-Captured "body-ipc-test" $bodyExe @("--ipc-test")
    if (-not $bodySelfOk -or -not $bodyIpcOk) { throw "body verification failed" }

    $selectedModes = @($faceModes)
    if ($SkipUi) {
        $selectedModes = @($selectedModes | Where-Object { $_ -notmatch '-ui-test$' })
    }
    $env:DEEPSEEK_API_KEY = ""
    $facePassed = 0
    foreach ($mode in $selectedModes) {
        $name = "face-" + $mode.TrimStart("-")
        if (Invoke-Captured $name $faceExe @($mode)) {
            $facePassed++
        }
        else {
            throw "face mode failed: $mode"
        }
    }
    Write-Log "FACE MODES passed=$facePassed total=$($selectedModes.Count)"

    $testProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '^AhzScribe\.(Face|Body|BrainHost)$'
    })
    $noResidue = $testProcesses.Count -eq 0
    Add-Step "process-residue" $noResidue $(if ($noResidue) { 0 } else { 1 }) ""

    $gitHead = (& git -C $repo rev-parse HEAD 2>$null)
    $gitBranch = (& git -C $repo branch --show-current 2>$null)
    $gitStatus = @(& git -C $repo status --porcelain 2>$null)
    $sourceClean = $gitStatus.Count -eq 0

    $m5aPass = $docsOk -and $issuesOk -and $buildsOk -and $brainOk -and
        $bodySelfOk -and $bodyIpcOk -and ($facePassed -eq $selectedModes.Count) -and
        $noResidue
    $report = [ordered]@{
        schema_version = 1
        gate = "M5-a"
        result = if ($m5aPass) { "M5A_PASS" } else { "FAIL" }
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        repo = $repo
        configuration = $Configuration
        dotnet = $dotnet
        dotnet_sdks = $sdkList
        git_branch = ($gitBranch -join "").Trim()
        git_head = ($gitHead -join "").Trim()
        source_clean = $sourceClean
        source_changes = $gitStatus.Count
        acceptance_docs = [ordered]@{
            ok = $docsOk
            required = $requiredDocs.Count
            missing = @($missingDocs)
        }
        issue_register = [ordered]@{
            ok = $issuesOk
            open_p0_p1 = $openBlocking.Count
            path = "docs/release/issue-register.json"
        }
        brain_core = $brainOk
        body_self_test = $bodySelfOk
        body_ipc = $bodyIpcOk
        face_modes_passed = $facePassed
        face_modes_total = $selectedModes.Count
        residue_processes = $testProcesses.Count
        steps = $steps.ToArray()
        next_gate = "M5-b 168-hour soak"
        release_ready = $false
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Log "RESULT $($report.result) report=$reportPath next=$($report.next_gate)"
    if (-not $m5aPass) { exit 1 }
    exit 0
}
catch {
    $message = $_.Exception.GetType().FullName + ": " + $_.Exception.Message
    Write-Log "FATAL $message"
    $fatalReport = [ordered]@{
        schema_version = 1
        gate = "M5-a"
        result = "FAIL"
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        fatal = $message
        steps = $steps.ToArray()
    }
    $fatalReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    exit 1
}
