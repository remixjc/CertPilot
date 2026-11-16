$ErrorActionPreference = 'Stop'
if (Get-Service CertPilot -ErrorAction SilentlyContinue) { Stop-Service CertPilot -Force -ErrorAction SilentlyContinue; sc.exe delete CertPilot | Out-Null }
Write-Host 'CertPilot service removed. Data and issued certificate files were preserved.'
