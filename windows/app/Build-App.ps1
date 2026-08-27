<#
.SYNOPSIS
    Builds CameraKillSwitch.exe from CameraKillSwitch.cs using the built-in .NET Framework
    C# compiler. No SDK download required.

.DESCRIPTION
    Produces a standalone Windows GUI executable in this folder. Just double-click
    the resulting CameraKillSwitch.exe to test, or deploy it alongside the install
    scripts for production use.
#>

[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutFile) { $OutFile = Join-Path $scriptDir 'CameraKillSwitch.exe' }

# Locate the newest .NET Framework 4.x csc.exe (ships with Windows).
$csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64\v4*\csc.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -Expand FullName
if (-not $csc) {
    $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework\v4*\csc.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -Expand FullName
}
if (-not $csc) { throw 'Could not find the .NET Framework C# compiler (csc.exe).' }
Write-Host "Using compiler: $csc"

$src      = Join-Path $scriptDir 'CameraKillSwitch.cs'
$manifest = Join-Path $scriptDir 'app.manifest'

# (Re)generate the application icon from the embedded camera artwork.
& powershell -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'Build-Icon.ps1')
$icon = Join-Path $scriptDir 'app.ico'

$args = @(
    '/target:winexe'
    "/out:$OutFile"
    '/optimize+'
    '/nologo'
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    "/win32manifest:$manifest"
)
if (Test-Path $icon) { $args += "/win32icon:$icon" }
$args += $src

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)." }

Write-Host ''
Write-Host "Built: $OutFile" -ForegroundColor Green
Write-Host 'Double-click it to test the camera switch.'
