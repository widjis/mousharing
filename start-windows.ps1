$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$Python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (-not (Test-Path $Python)) { throw 'Run setup-windows.ps1 first.' }
if (-not (Test-Path 'data\windows-cert.pem') -or -not (Test-Path 'data\windows-key.pem')) {
    throw 'Receiver certificate missing. Run setup-windows.ps1 first.'
}
Write-Host 'Candidate Windows LAN addresses (choose your Wi-Fi/Ethernet adapter):'
Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
    $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*'
} | Select-Object InterfaceAlias, IPAddress | Format-Table -AutoSize
Write-Host 'Use the same private pairing password (at least 16 characters) on both laptops.'
& $Python mousharing.py receive --cert data\windows-cert.pem --key data\windows-key.pem
exit $LASTEXITCODE
