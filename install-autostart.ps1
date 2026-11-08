$ErrorActionPreference = "Stop"
$startup = [Environment]::GetFolderPath("Startup")
$lnk = Join-Path $startup "AhzScribe.lnk"
$target = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$script = Join-Path $PSScriptRoot "start.ps1"
if (-not (Test-Path $script)) { throw "start.ps1 not found: $script" }

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath = $target
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = "AhzScribe silent boot"
$shortcut.Save()
Write-Host "Startup shortcut installed: $lnk"
