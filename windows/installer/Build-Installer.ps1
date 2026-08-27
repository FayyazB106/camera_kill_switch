<#
.SYNOPSIS
    Compiles CameraKillSwitch-Setup.exe from CameraKillSwitch.iss using Inno Setup.

.DESCRIPTION
    Ensures the app is built (CameraKillSwitch.exe present), locates ISCC.exe,
    then compiles the installer to <repo>\dist\CameraKillSwitch-Setup.exe.

    Install Inno Setup once with:  winget install JRSoftware.InnoSetup
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# 1. Make sure the app exe exists (build it if not).
$exe = Join-Path $here '..\app\CameraKillSwitch.exe'
if (-not (Test-Path $exe)) {
    Write-Host 'App exe not found; building it...' -ForegroundColor Yellow
    & powershell -ExecutionPolicy Bypass -File (Join-Path $here '..\app\Build-App.ps1')
}
if (-not (Test-Path $exe)) { throw "Build failed: $exe not found." }

# 2. Locate the Inno Setup compiler.
$candidates = @(
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup:  winget install JRSoftware.InnoSetup"
}
Write-Host "Using compiler: $iscc" -ForegroundColor DarkGray

# 3. Compile.
$iss = Join-Path $here 'CameraKillSwitch.iss'
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$out = Join-Path $here '..\..\dist\CameraKillSwitch-Setup.exe'
if (Test-Path $out) {
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 2)
    Write-Host ''
    Write-Host "Built: $((Resolve-Path $out).Path)  ($mb MB)" -ForegroundColor Green
} else {
    throw 'Compile reported success but output exe is missing.'
}
