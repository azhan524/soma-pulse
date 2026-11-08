param(
    [string]$TaskName = "AhzScribe-M5B-Soak",
    [string]$OutputDir = "",
    [int]$SampleSeconds = 60
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$soakScript = Join-Path $repo "tools\soak\m3d-soak.ps1"
if (-not (Test-Path -LiteralPath $soakScript)) {
    throw "soak script missing: $soakScript"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo ("_local_artifacts\soak\m5b-official-" +
        (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    throw "scheduled task already exists: $TaskName"
}
$existingFace = Get-Process AhzScribe.Face -ErrorAction SilentlyContinue
if ($existingFace) {
    throw "AhzScribe.Face is already running (PID $($existingFace.Id)); M5-b requires an isolated process"
}

$arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Gate M5B -Hours 168 -SampleSeconds {1} -OutputDir "{2}"' -f
    $soakScript, $SampleSeconds, $OutputDir
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument $arguments -WorkingDirectory $repo
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Description "AhzScribe M5-b 168-hour isolated soak" | Out-Null
Start-ScheduledTask -TaskName $TaskName

$task = Get-ScheduledTask -TaskName $TaskName
[pscustomobject]@{
    TaskName = $TaskName
    State = $task.State
    OutputDir = $OutputDir
    Script = $soakScript
    Hours = 168
    SampleSeconds = $SampleSeconds
} | Format-List
