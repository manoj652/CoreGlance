# Removes CoreGlance: running copies, autostart, Start Menu shortcut, files, and the sensor-mode task.
#   -KeepSettings   keep %APPDATA%\CoreGlance (position, view, options)
param([switch]$KeepSettings)

$ErrorActionPreference = 'Continue'

Get-Process CoreGlance -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit(3000) } catch { } }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CoreGlance -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'CoreGlance.lnk') -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\CoreGlance') -Recurse -Force -ErrorAction SilentlyContinue

# Admin parts (sensor mode): scheduled task + Program Files copy.
cmd.exe /c "schtasks.exe /Query /TN CoreGlance >nul 2>&1"
$taskExists = $LASTEXITCODE -eq 0
$pf = Join-Path $env:ProgramFiles 'CoreGlance'
if ($taskExists -or (Test-Path $pf)) {
    Write-Host 'Removing sensor mode (approve the UAC prompt)...'
    $cmd = "Stop-Process -Name CoreGlance -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; " +
           "schtasks.exe /Delete /TN CoreGlance /F | Out-Null; Remove-Item -LiteralPath '$pf' -Recurse -Force -ErrorAction SilentlyContinue"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    try { Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList "-NoProfile -EncodedCommand $enc" }
    catch { Write-Warning 'UAC was declined; the sensor task and Program Files copy were left in place.' }
}

if (-not $KeepSettings) { Remove-Item (Join-Path $env:APPDATA 'CoreGlance') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'CoreGlance removed.'
