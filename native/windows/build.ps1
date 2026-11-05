param([ValidateSet('x64','ARM64')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build aplikasi Windows harus dijalankan pada Windows.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Pasang .NET 10 SDK dari Microsoft terlebih dahulu.' }
$SdkVersion = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($SdkVersion.Split('.')[0]) -lt 10) { throw '.NET 10 SDK diperlukan.' }
$Runtime = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$Project = Join-Path $PSScriptRoot 'Mousharing.Windows\Mousharing.Windows.csproj'
$OutputDirectory = Join-Path $PSScriptRoot "dist\$Runtime"
& dotnet publish $Project -c Release -r $Runtime -p:Platform=$Architecture --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Build gagal; periksa pesan compiler di atas.' }
Write-Host "Aplikasi siap diuji: $OutputDirectory\Mousharing.Windows.exe"
