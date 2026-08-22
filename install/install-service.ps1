<#
.SYNOPSIS
  Installs EmployeeAgent.Service as a real Windows Service with OS-enforced
  auto-restart, replacing the old basic watchdog.

.DESCRIPTION
  Run this AS ADMINISTRATOR after publishing both EmployeeAgent and
  EmployeeAgent.Service:

      cd EmployeeAgent
      dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
      cd ..\EmployeeAgent.Service
      dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"

  This script creates the service (running as LocalSystem, auto-start) and
  configures `sc failure` recovery so the Service Control Manager itself
  restarts it if it's ever killed - this is the actual "basic tier to
  OS-enforced tier" upgrade over the old watchdog .exe.

.PARAMETER ServicePath
  Path to the published EmployeeAgent.Service.exe.

.NOTES
  This script does NOT configure Group Policy restrictions on who can run
  `sc stop`/`net stop EmployeeAgentService`. That's a tenant-specific setting
  your IT admins configure in Active Directory (Computer Configuration >
  Windows Settings > Security Settings > System Services), not something
  that can be committed as code in this repo. Without it, a local
  administrator can still stop the service - `sc failure` protects against
  the process being killed/crashing, not against an admin deliberately
  disabling it.
#>
param(
    [string]$ServicePath = "C:\Program Files\EmployeeAgent\EmployeeAgent.Service.exe",
    [string]$ServiceName = "EmployeeAgentService"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator."
}

if (-not (Test-Path $ServicePath)) {
    throw "Service executable not found at '$ServicePath'. Publish it first (see script header for the commands)."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists - stopping and removing it first."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service '$ServiceName' -> $ServicePath"
sc.exe create $ServiceName binPath= "`"$ServicePath`"" start= auto obj= LocalSystem DisplayName= "Employee Agent Supervisor" | Out-Null

Write-Host "Configuring OS-enforced auto-restart on failure (sc failure)"
# reset=0 means the failure counter never resets, so every crash keeps
# triggering the restart action instead of only the first 3 within a window.
sc.exe failure $ServiceName reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Write-Host "Starting service"
Start-Service -Name $ServiceName

Write-Host "`nDone. '$ServiceName' is installed and running."
Write-Host "Verify with:  Get-Service $ServiceName"
Write-Host "Test recovery by killing EmployeeAgent.exe in Task Manager - it should reappear within ~30s."
