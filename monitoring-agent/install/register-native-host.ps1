<#
.SYNOPSIS
  Registers the EmployeeAgent Native Messaging host so the browser-extension/
  extension can talk to it over stdio.

.DESCRIPTION
  Writes the native-messaging host manifest JSON and registers it in the
  registry for Chrome and Edge (both use the same native-messaging
  mechanism). Must be run AFTER the extension has been loaded at least once,
  since the extension's ID (assigned by the browser) has to go into
  "allowed_origins" - find it at chrome://extensions or edge://extensions
  with Developer mode on.

.PARAMETER ExtensionId
  The extension ID shown at chrome://extensions (or edge://extensions).
  Required.

.PARAMETER HostExePath
  Path to the published EmployeeAgent.NativeHost.exe. Defaults to the
  standard install location used by install-service.ps1.

.EXAMPLE
  .\register-native-host.ps1 -ExtensionId "abcdefghijklmnopabcdefghijklmnop"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ExtensionId,

    [string]$HostExePath = "C:\Program Files\EmployeeAgent\EmployeeAgent.NativeHost.exe"
)

$ErrorActionPreference = "Stop"

$hostName = "com.employeeagent.nativehost"

if (-not (Test-Path $HostExePath)) {
    Write-Warning "Native host executable not found at '$HostExePath'. Publish it first:`n  cd EmployeeAgent.NativeHost`n  dotnet publish -c Release -o `"C:\Program Files\EmployeeAgent`""
}

$manifestObject = @{
    name            = $hostName
    description     = "EmployeeAgent native messaging host - full URL activity reporting"
    path            = $HostExePath
    type            = "stdio"
    allowed_origins = @("chrome-extension://$ExtensionId/")
}

$manifestDir = "C:\Program Files\EmployeeAgent"
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
$manifestPath = Join-Path $manifestDir "$hostName.json"
$manifestObject | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "Wrote native host manifest to $manifestPath"

# Chrome and Edge both read this same registry shape, under their own hive.
# HKCU registers the host for the current user only; switch to HKLM if you
# need it available machine-wide for all users (requires running elevated).
$registryRoots = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
)

foreach ($regPath in $registryRoots) {
    New-Item -Path $regPath -Force | Out-Null
    Set-Item -Path $regPath -Value $manifestPath
    Write-Host "Registered native host at $regPath -> $manifestPath"
}

Write-Host "`nDone. Reload the extension (or restart the browser) for the native host to be reachable."
