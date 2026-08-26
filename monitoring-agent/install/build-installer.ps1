<#
.SYNOPSIS
  Builds EmployeeAgent.msi - a single installer bundling the agent, the
  anti-tamper Windows Service, and the native messaging host - for
  distribution to a company's IT department (GPO Software Installation,
  Intune Win32 app, or a remote-exec/RMM tool). See "Packaging a
  distributable installer" in AGENT.md for the full rollout workflow.

.DESCRIPTION
  1. `dotnet publish`-es EmployeeAgent, EmployeeAgent.Service and
     EmployeeAgent.NativeHost into one shared staging folder (same thing
     the manual docs in AGENT.md/README do by hand, just scripted).
  2. Builds EmployeeAgent.Installer\Product.wxs against that staging folder,
     baking in -BackendUrl as the default EMPLOYEEAGENT_BACKEND_URL.

  Requires: .NET 8 SDK, and the WiX v4 CLI (`dotnet tool install --global wix`
  + `wix extension add WixToolset.Util.wixext`) on the machine running this
  script. Must be run on Windows - the three agent projects target
  net8.0-windows and won't build elsewhere.

.PARAMETER BackendUrl
  Backend URL to bake in as the default EMPLOYEEAGENT_BACKEND_URL for every
  machine this MSI is installed on (e.g. "https://backend.acme-corp.com").
  Can still be overridden per-machine with
  `msiexec /i EmployeeAgent.msi BACKENDURL=...`. Pass "" to leave it unset
  in the MSI and configure it some other way after install (registry push,
  GPO Preferences, etc.).

.PARAMETER Version
  MSI product version (also used for upgrade detection - bump this on every
  rebuild you intend to roll out as an update).

.EXAMPLE
  .\build-installer.ps1 -BackendUrl "https://backend.acme-corp.com" -Version "1.0.1"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendUrl,

    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$monitoringAgentRoot = Split-Path $PSScriptRoot -Parent
$stagingDir = Join-Path $monitoringAgentRoot "publish\EmployeeAgent"

if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

foreach ($project in "EmployeeAgent", "EmployeeAgent.Service", "EmployeeAgent.NativeHost") {
    Write-Host "Publishing $project ..."
    Push-Location (Join-Path $monitoringAgentRoot $project)
    try {
        dotnet publish -c Release -o $stagingDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Building EmployeeAgent.msi ..."
Push-Location (Join-Path $PSScriptRoot "EmployeeAgent.Installer")
try {
    dotnet build -c Release `
        -p:StagingDir=$stagingDir `
        -p:BackendUrl=$BackendUrl `
        -p:ProductVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
}
finally {
    Pop-Location
}

$msiPath = Join-Path $PSScriptRoot "EmployeeAgent.Installer\bin\Release\EmployeeAgent.msi"
Write-Host "`nDone: $msiPath"
Write-Host "Silent install:   msiexec /i `"$msiPath`" /quiet"
Write-Host "Override backend: msiexec /i `"$msiPath`" /quiet BACKENDURL=https://other-backend.example.com"
