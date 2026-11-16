#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$data = Join-Path $env:RUNNER_TEMP ('CertPilot-smoke-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $env:ProgramFiles 'CertPilot'
$env:CERTPILOT_SMOKE_ENDPOINT = 'https://localhost:18443'
$env:CERTPILOT_SMOKE_DATA = $data
$env:CERTPILOT_SMOKE_OUTPUT = Join-Path (Split-Path $package) 'smoke-evidence'
try {
    & (Join-Path $package 'scripts\install-service.ps1') -SourceDirectory $package -InstallDir $install -DataDirectory $data -Port 18443 -NoShortcut
    # Launch installed WPF outside its own directory to catch current-working-directory assumptions.
    $process = Start-Process -FilePath (Join-Path $install 'CertPilot.exe') -ArgumentList '--smoke-test' -WorkingDirectory $env:TEMP -PassThru
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'WPF smoke test timed out.' }
    if ($process.ExitCode -ne 0) {
        $errorFile = Join-Path $env:CERTPILOT_SMOKE_OUTPUT 'desktop-smoke-error.txt'
        if (Test-Path $errorFile) { Get-Content $errorFile | Write-Host }
        throw "WPF smoke test failed: $($process.ExitCode)"
    }
    if ((Get-Service CertPilot).Status -ne 'Running') { throw 'Closing WPF stopped the service.' }
    $web = & curl.exe --silent --fail --insecure --max-time 10 'https://localhost:18443/'
    if ($LASTEXITCODE -ne 0 -or $web -notmatch 'CertPilot') { throw 'Packaged Web UI is missing.' }
    Write-Host 'PASS: native WPF startup/render; service registration/start; pinned HTTPS; bootstrap login/password change; credential/certificate CRUD; revision conflict; service remains running after WPF exits; packaged Web UI.'
}
finally {
    & (Join-Path $package 'scripts\uninstall-service.ps1')
    Remove-Item Env:CERTPILOT_SMOKE_ENDPOINT, Env:CERTPILOT_SMOKE_DATA, Env:CERTPILOT_SMOKE_OUTPUT -ErrorAction SilentlyContinue
}
