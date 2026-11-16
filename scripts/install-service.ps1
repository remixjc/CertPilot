param([string]$InstallDir = "$env:ProgramFiles\CertPilot", [string]$Binary = "$PSScriptRoot\..\artifacts\CertPilot-win-x64\CertPilot.Service.exe")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item -Path (Join-Path (Split-Path $Binary) '*') -Destination $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir 'CertPilot.Service.exe'
if (Get-Service CertPilot -ErrorAction SilentlyContinue) { Stop-Service CertPilot -Force -ErrorAction SilentlyContinue; sc.exe delete CertPilot | Out-Null }
sc.exe create CertPilot binPath= "`"$exe`"" start= auto DisplayName= "CertPilot Certificate Automation" | Out-Null
sc.exe description CertPilot "ACME certificate issuance and renewal service with local Web API" | Out-Null
sc.exe failure CertPilot reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
Start-Service CertPilot
Write-Host "CertPilot installed at $InstallDir. Open https://localhost:8443/ and read the bootstrap-admin.txt file in the service data directory."
