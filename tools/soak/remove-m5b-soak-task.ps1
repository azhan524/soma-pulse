param(
    [string]$TaskName = "AhzScribe-M5B-Soak"
)

$ErrorActionPreference = "Stop"
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    "Scheduled task not found: $TaskName"
    exit 0
}

if ($task.State -eq "Running") {
    Stop-ScheduledTask -TaskName $TaskName
}
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
"Removed scheduled task: $TaskName"
