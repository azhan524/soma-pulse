$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "AhzScribe.lnk"
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "Startup shortcut removed: $lnk" }
else { Write-Host "No startup shortcut: $lnk" }
