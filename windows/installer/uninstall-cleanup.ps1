<#
.SYNOPSIS
    Runtime teardown for Camera Kill Switch, called by the uninstaller
    (Inno [UninstallRun]) before files are removed. Runs elevated.

    The installer only lays down files + an autostart value; the app itself
    creates the scheduled tasks and the %ProgramData%\CameraKillSwitch engine
    folder on first use. This script removes those runtime artifacts and makes
    sure the camera is left ENABLED, so uninstalling never strands a user with
    their camera off.

    Everything here is best-effort: a machine where the app was installed but
    never toggled won't have tasks to remove, and that's fine.
#>

$ErrorActionPreference = 'SilentlyContinue'

# 1. Re-enable the camera first, via the app's own elevated task (if present).
Start-Process schtasks.exe -ArgumentList '/run', '/tn', 'CameraKillSwitch-Enable' `
    -WindowStyle Hidden -Wait
Start-Sleep -Seconds 2

# 2. Stop the running tray app so its exe isn't locked when files are removed.
Get-Process -Name 'CameraKillSwitch' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
# Legacy PowerShell-based tray, if it was ever used.
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*CameraTray.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# 3. Remove the elevated scheduled tasks (current + legacy names).
foreach ($t in 'CameraKillSwitch-Disable', 'CameraKillSwitch-Enable',
                'CameraSwitch-Disable',     'CameraSwitch-Enable') {
    Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
}

# 4. Remove the runtime engine folders under ProgramData.
foreach ($d in 'CameraKillSwitch', 'CameraSwitch') {
    Remove-Item -Path (Join-Path $env:ProgramData $d) -Recurse -Force -ErrorAction SilentlyContinue
}

exit 0
