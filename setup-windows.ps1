$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($env:OS -ne 'Windows_NT') { throw 'Run this script on Windows.' }
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        & py -3 -m venv .venv
    } elseif (Get-Command python -ErrorAction SilentlyContinue) {
        & python -m venv .venv
    } else { throw 'Install Python 3.11 or newer from python.org, then run setup again.' }
    if ($LASTEXITCODE -ne 0) { throw 'Could not create virtual environment.' }
}
$Python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
& $Python -c 'import sys; sys.exit(int(sys.version_info < (3, 9)))'
if ($LASTEXITCODE -ne 0) { throw 'Unsupported Python version.' }
& $Python -m pip install -r requirements.txt
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
& $Python scripts\init_certificate.py
if ($LASTEXITCODE -ne 0) { throw 'Certificate setup failed.' }
Write-Host ''
Write-Host 'Setup complete. Copy data\windows-cert.pem to the Mac data folder.'
Write-Host 'Start receiver: powershell -NoProfile -ExecutionPolicy Bypass -File .\start-windows.ps1'
