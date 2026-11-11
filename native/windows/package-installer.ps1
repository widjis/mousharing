param([Parameter(Mandatory)][string]$PublishDirectory, [string]$OutputDirectory = "$PSScriptRoot\dist\installers")
$ErrorActionPreference = 'Stop'
$Version = (Get-Content "$PSScriptRoot\..\VERSION" -Raw).Trim()
$PublishDirectory = (Resolve-Path $PublishDirectory).Path
if (-not (Test-Path "$PublishDirectory\Mousharing.Windows.exe")) { throw 'Publish folder does not contain Mousharing.Windows.exe.' }
$Compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($Compiler) { $Iscc = $Compiler.Source }
else { $Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $Iscc)) { throw 'Inno Setup 6 is required on the packaging machine.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
& $Iscc "/DAppVersion=$Version" "/DPublishDir=$PublishDirectory" "/DOutputPath=$OutputDirectory" "$PSScriptRoot\installer\Mousharing.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compiler failed.' }
$Installer = Join-Path $OutputDirectory "Mousharing-$Version-Windows-x64-Setup.exe"
$Hash = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$Hash  $(Split-Path $Installer -Leaf)" | Set-Content "$Installer.sha256" -Encoding ascii
Write-Host "Installer: $Installer"
