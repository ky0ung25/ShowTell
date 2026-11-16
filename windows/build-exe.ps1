# Builds the portable single-file ShowTell.exe (no install, no .NET needed) into windows\dist.
#   powershell -ExecutionPolicy Bypass -File build-exe.ps1 [-Version 1.2.0] [-NoZip]
# -NoZip leaves only ShowTell.exe, so it can be code-signed before zipping (as the GitHub workflow does).
param([string]$Version = '0.1.0', [switch]$NoZip)
$ErrorActionPreference = 'Stop'

$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

& $dotnet publish "$PSScriptRoot\ShowTell.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not $NoZip) {
    Compress-Archive -Path (Join-Path $dist 'ShowTell.exe') -DestinationPath (Join-Path $dist 'ShowTell-windows-x64.zip') -Force
}
Get-ChildItem $dist | ForEach-Object { '{0,-28} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
