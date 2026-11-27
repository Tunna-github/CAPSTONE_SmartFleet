param([switch]$InstallStartup)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPrivate = Join-Path $env:LOCALAPPDATA 'SmartFleet\aiven-migration'
New-Item -ItemType Directory -Path $taskPrivate -Force | Out-Null
$taskWorker = Join-Path $PSScriptRoot 'map_switch_worker.py'
$taskPython = (& python -c 'import sys; print(sys.executable)').Trim()
if ($InstallStartup) {
    $taskShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'SmartFleet Map Manager.lnk'
    $taskShell = New-Object -ComObject WScript.Shell
    $taskLink = $taskShell.CreateShortcut($taskShortcut)
    $taskPythonw = Join-Path (Split-Path -Parent $taskPython) 'pythonw.exe'
    if (!(Test-Path -LiteralPath $taskPythonw)) { throw 'pythonw.exe is required for a background startup worker.' }
    $taskLink.TargetPath = $taskPythonw
    $taskLink.Arguments = '"' + $taskWorker + '"'
    $taskLink.WorkingDirectory = $taskRoot
    $taskLink.WindowStyle = 7
    $taskLink.Save()
    Write-Output 'Installed current-user startup shortcut for the map manager.'
}
$taskPidFile = Join-Path $taskPrivate 'map-switch-worker.pid'
if (Test-Path -LiteralPath $taskPidFile) {
    $taskWorkerId = Get-Content -LiteralPath $taskPidFile
    if ($taskWorkerId -match '^\d+$') {
        $taskProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$taskWorkerId"
        if ($taskProcess -and $taskProcess.CommandLine.Contains($taskWorker)) {
            Write-Output 'Map manager is already running.'
            exit 0
        }
    }
}
Start-Process -FilePath $taskPython -WindowStyle Hidden -WorkingDirectory $taskRoot `
    -ArgumentList @('-u', ('"' + $taskWorker + '"')) `
    -RedirectStandardOutput (Join-Path $taskPrivate 'map-manager.stdout.log') `
    -RedirectStandardError (Join-Path $taskPrivate 'map-manager.stderr.log')
Write-Output 'Started host map manager in the background.'
