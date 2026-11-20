# Builds dist\CoreGlance.exe with the C# compiler that ships inside Windows (.NET Framework 4.x).
# No SDK, Visual Studio or NuGet needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found. .NET Framework 4.x is part of Windows 10/11; enable it in Windows Features.' }

$icon = Join-Path $root 'assets\CoreGlance.ico'
if (-not (Test-Path $icon)) { & (Join-Path $root 'tools\make-icon.ps1') -Out $icon }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist 'CoreGlance.exe'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
# Official signed PawnIO modules (LGPL-2.1), embedded as "pawnio.<file>"; used only if the user installs PawnIO.
$resources = Get-ChildItem (Join-Path $root 'assets\pawnio') -Filter *.bin -ErrorAction SilentlyContinue |
    ForEach-Object { "/resource:$($_.FullName),pawnio.$($_.Name)" }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /debug- /warn:4 `
    "/out:$out" "/win32icon:$icon" "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    /codepage:65001 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    $resources $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)" }

$kb = [Math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "Built $out ($kb KB)"
