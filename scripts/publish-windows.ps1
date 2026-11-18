param(
    [string]$Version = '0.1.2',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\CertPilot-win-x64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Invalid version: $Version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw "Output already exists: $output. Choose an empty output directory." }
New-Item -ItemType Directory -Path $output | Out-Null
& dotnet publish (Join-Path $root 'src\CertPilot.Desktop\CertPilot.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:Version=$Version" -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed.' }
$service = Join-Path $output 'service'
& dotnet publish (Join-Path $root 'src\CertPilot.Service\CertPilot.Service.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:Version=$Version" -p:DebugType=None -o $service
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
# Keep all Web and configuration files physical; never rely on bundler content extraction.
Copy-Item (Join-Path $root 'src\CertPilot.Service\wwwroot') $service -Recurse -Force
Copy-Item (Join-Path $root 'src\CertPilot.Service\appsettings.json') $service -Force
Copy-Item (Join-Path $root 'src\CertPilot.Service\appsettings.Local.example.json') $service -Force
New-Item -ItemType Directory (Join-Path $output 'scripts') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'install-service.ps1'), (Join-Path $PSScriptRoot 'uninstall-service.ps1') (Join-Path $output 'scripts')
Copy-Item (Join-Path $root 'README.md') $output
Copy-Item (Join-Path $root 'docs\windows-quickstart.txt') (Join-Path $output '先读我-使用说明.txt')
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""%~dp0scripts\install-service.ps1""'"
if errorlevel 1 pause
'@ | Set-Content (Join-Path $output '安装服务.cmd') -Encoding ASCII
foreach ($file in @('CertPilot.exe', 'service\CertPilot.Service.exe', 'service\appsettings.json', 'service\wwwroot\index.html', 'service\wwwroot\api-docs.html', 'scripts\install-service.ps1')) {
    if (!(Test-Path (Join-Path $output $file))) { throw "Release package is incomplete: $file" }
}
Write-Host "Complete WPF + service package: $output"
