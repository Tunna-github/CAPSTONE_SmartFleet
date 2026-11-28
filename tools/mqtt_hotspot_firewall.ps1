param(
    [string]$HotspotAddress = '192.168.137.1',
    [string[]]$RobotAddress = @('192.168.137.168'),
    [ValidateRange(1,65535)][int]$Port = 1883,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$taskLocal = [Net.IPAddress]::Parse($HotspotAddress)
if ($taskLocal.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    !(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object IPAddress -eq $HotspotAddress)) {
    throw 'The specified hotspot IPv4 address is not active.'
}
foreach ($taskRobot in $RobotAddress) {
    $taskParsed = [Net.IPAddress]::Parse($taskRobot)
    if ($taskParsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        $taskParsed.Equals([Net.IPAddress]::Any) -or $taskParsed.Equals([Net.IPAddress]::Loopback) -or
        $taskParsed.Equals($taskLocal)) { throw 'Provide individual robot IPv4 addresses.' }
}
$taskRuleName = 'SmartFleet-Docker-MQTT-Hotspot'
Write-Output "Scope: TCP $HotspotAddress`:$Port from $($RobotAddress -join ', ') only."
if (!$Apply) {
    Write-Output 'Inspection only. Use -Apply in an Administrator PowerShell if the actual robot cannot connect.'
    Get-NetFirewallRule -Name $taskRuleName -ErrorAction SilentlyContinue | Select-Object Name,Enabled,Direction,Action
    exit 0
}
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Firewall configuration requires Administrator PowerShell once. No rule was changed.'
}
$taskOld = Get-NetFirewallRule -Name $taskRuleName -ErrorAction SilentlyContinue
if ($taskOld) {
    Set-NetFirewallRule -Name $taskRuleName -Enabled True -Direction Inbound -Action Allow -Profile Any `
        -Protocol TCP -LocalAddress $HotspotAddress -LocalPort $Port -RemoteAddress $RobotAddress | Out-Null
} else {
    New-NetFirewallRule -Name $taskRuleName -DisplayName 'SmartFleet Docker MQTT - hotspot robots' `
        -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP `
        -LocalAddress $HotspotAddress -LocalPort $Port -RemoteAddress $RobotAddress | Out-Null
}
Write-Output 'Applied the scoped MQTT rule. Firewall remains enabled; no service or robot was changed.'
