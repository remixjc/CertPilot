#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\CertPilot",
    [string]$SourceDirectory = (Split-Path $PSScriptRoot),
    [string]$DataDirectory,
    [int]$Port = 8443,
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
function Invoke-ServiceCommand([string[]]$Arguments) {
    & sc.exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Service command failed: $($Arguments -join ' ')" }
}
$source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
$target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
foreach ($file in @('CertPilot.exe', 'service\CertPilot.Service.exe', 'service\wwwroot\index.html')) {
    if (!(Test-Path (Join-Path $source $file))) { throw "Incomplete package: $file. Extract the entire Windows ZIP first." }
}
$service = Get-Service CertPilot -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service CertPilot
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}
New-Item -ItemType Directory -Force $target | Out-Null
if (![string]::Equals($source, $target, [StringComparison]::OrdinalIgnoreCase)) {
    # Preserve installation-local configuration on updates. Database and issued files live outside Program Files.
    Get-ChildItem $source -File | Copy-Item -Destination $target -Force
    foreach ($dir in @('service', 'scripts')) {
        $destination = Join-Path $target $dir
        New-Item -ItemType Directory -Force $destination | Out-Null
        foreach ($item in (Get-ChildItem (Join-Path $source $dir))) {
            if ($item.Name -eq 'appsettings.Local.json') { continue }
            Copy-Item $item.FullName $destination -Recurse -Force
        }
    }
}
$configPath = Join-Path $target 'service\appsettings.Local.json'
# Migrate the original service-only installation's local settings into the new service subdirectory.
$legacyConfig = Join-Path $target 'appsettings.Local.json'
if (!(Test-Path $configPath) -and (Test-Path $legacyConfig)) { Copy-Item $legacyConfig $configPath }
$config = if (Test-Path $configPath) { Get-Content $configPath -Raw | ConvertFrom-Json } else { [PSCustomObject]@{ CertPilot = [PSCustomObject]@{} } }
if (!$config.CertPilot) { throw 'appsettings.Local.json must contain a CertPilot section.' }
if ($PSBoundParameters.ContainsKey('DataDirectory')) {
    if ($service) { throw 'Changing DataDirectory during an update is not supported. Preserve the current data directory.' }
    $config.CertPilot | Add-Member NoteProperty DataDirectory ([IO.Path]::GetFullPath($DataDirectory)) -Force
}
if ($PSBoundParameters.ContainsKey('Port') -or !(Test-Path $configPath)) {
    $config.CertPilot | Add-Member NoteProperty Port $Port -Force
}
if (!$config.CertPilot.DataDirectory) { $config.CertPilot | Add-Member NoteProperty DataDirectory "$env:ProgramData\CertPilot" -Force }
$config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8
$data = $config.CertPilot.DataDirectory
if (![IO.Path]::IsPathRooted($data) -or $data.StartsWith('\\') -or [IO.Path]::GetFullPath($data).TrimEnd('\') -eq [IO.Path]::GetPathRoot($data).TrimEnd('\')) { throw 'DataDirectory must be a dedicated local directory.' }
New-Item -ItemType Directory -Force $data | Out-Null
# DPAPI LocalMachine still requires a protected data directory. Use SID-based ACLs on all locales.
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $data -AclObject $acl
$exe = Join-Path $target 'service\CertPilot.Service.exe'
if ($service) { Invoke-ServiceCommand -Arguments @('config', 'CertPilot', 'binPath=', "`"$exe`"", 'start=', 'auto') }
else { Invoke-ServiceCommand -Arguments @('create', 'CertPilot', 'binPath=', "`"$exe`"", 'start=', 'auto', 'DisplayName=', 'CertPilot Certificate Automation') }
Invoke-ServiceCommand -Arguments @('description', 'CertPilot', 'ACME certificate automation. Manage with CertPilot.exe or the Web API.')
Invoke-ServiceCommand -Arguments @('failure', 'CertPilot', 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/60000')
Start-Service CertPilot
(Get-Service CertPilot).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
$deadline = (Get-Date).AddSeconds(60)
$health = "https://localhost:$($config.CertPilot.Port)/health/live"
if ($config.CertPilot.DevelopmentHttp) { $health = "http://localhost:$($config.CertPilot.Port)/health/live" }
# curl checks listener availability only; the desktop authenticates the exact local management.cer certificate.
$healthy = $false
while ((Get-Date) -lt $deadline) {
    & curl.exe --silent --fail --insecure --max-time 2 $health > $null 2>&1
    if ($LASTEXITCODE -eq 0) { $healthy = $true; break }
    Start-Sleep -Milliseconds 500
}
if (!$healthy) { throw 'The Windows service started but the Web listener is unavailable. Check local TLS, listen address, port, and Windows Event Viewer.' }
if (!$NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $programs = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'CertPilot'
    New-Item -ItemType Directory -Force $programs | Out-Null
    foreach ($shortcutPath in @((Join-Path $programs 'CertPilot.lnk'), (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'CertPilot.lnk'))) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $target 'CertPilot.exe'
        $shortcut.WorkingDirectory = $target
        $shortcut.Description = 'CertPilot WPF Certificate Manager'
        $shortcut.Save()
    }
}
Write-Host "Installed and running. Open $target\CertPilot.exe. Bootstrap credentials: $data\bootstrap-admin.txt"
