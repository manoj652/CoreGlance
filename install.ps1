# Installs CoreGlance for the current user (no admin needed) and starts it.
#   -NoAutostart     don't start with Windows
#   -EnableSensors   also turn on admin sensor mode (fan RPM + firmware CPU/GPU temps); asks for UAC once
param([switch]$NoAutostart, [switch]$EnableSensors)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'dist\CoreGlance.exe'
if (-not (Test-Path $src)) { & (Join-Path $root 'build.ps1') }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\CoreGlance'
$exe = Join-Path $dest 'CoreGlance.exe'

# Stop the normal-user copy (an admin copy, if any, is replaced by --install-task below).
Get-Process CoreGlance -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit(3000) } catch { } }
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $src $exe -Force

# Start Menu shortcut with the app id, so a pinned icon and the running window are one app.
Start-Process $exe -ArgumentList '--make-shortcut' -Wait

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutostart) { Remove-ItemProperty $runKey -Name CoreGlance -ErrorAction SilentlyContinue }
else { Set-ItemProperty $runKey -Name CoreGlance -Value "`"$exe`" --autostart" }

# Sensor mode: on request, or refresh the admin copy if it was enabled before.
cmd.exe /c "schtasks.exe /Query /TN CoreGlance >nul 2>&1"
$taskExists = $LASTEXITCODE -eq 0
if ($EnableSensors -or $taskExists) {
    Write-Host 'Setting up admin sensor mode (approve the UAC prompt)...'
    try {
        $p = Start-Process $exe -ArgumentList '--install-task' -Verb RunAs -PassThru -Wait
        if ($p.ExitCode -ne 0) { Write-Warning "Sensor mode setup failed (exit $($p.ExitCode)). See $env:APPDATA\CoreGlance\error.log" }
    } catch { Write-Warning 'UAC was declined; CoreGlance runs without fan readings.' }
}

Start-Process $exe
Write-Host "Installed to $dest"
