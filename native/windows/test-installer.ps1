param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path $Installer).Path
$Target = Join-Path $env:TEMP ('Mousharing-install-test-' + [guid]::NewGuid().ToString('N'))
$Log = "$Target.log"
function Run-Checked([string]$File, [string]$Arguments) {
    $Process = Start-Process $File -ArgumentList $Arguments -PassThru
    if (-not $Process.WaitForExit(120000)) { Stop-Process -Id $Process.Id -Force; throw "Installer test timed out: $File" }
    if ($Process.ExitCode -ne 0) { throw "Exit code $($Process.ExitCode): $File" }
}
try {
    Run-Checked $Installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /DIR=`"$Target`" /LOG=`"$Log`""
    if (-not (Test-Path "$Target\Mousharing.Windows.exe")) { throw 'Installed executable missing.' }
    Run-Checked "$Target\Mousharing.Windows.exe" '--ui-smoke-test'
    Run-Checked "$Target\unins000.exe" '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    if (Test-Path "$Target\Mousharing.Windows.exe") { throw 'Uninstall left the app executable.' }
    Write-Host 'PASS silent per-user installation, installed WinUI startup, and uninstallation.'
} finally {
    # Only the unique CI test installation directory, never user config.
    if (Test-Path "$Target\unins000.exe") { & "$Target\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART | Out-Null }
}
