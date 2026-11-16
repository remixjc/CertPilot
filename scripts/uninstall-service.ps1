#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$service = Get-Service CertPilot -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service CertPilot; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
    & sc.exe delete CertPilot
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove CertPilot service.' }
}
foreach ($shortcut in @((Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'CertPilot\CertPilot.lnk'), (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'CertPilot.lnk'))) {
    if (Test-Path $shortcut) { Remove-Item $shortcut }
}
Write-Host 'Service and shortcuts removed. Application files, configuration, database and certificates were preserved.'
