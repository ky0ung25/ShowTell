# Builds ShowTell as a self-contained app (no .NET install needed to run it),
# copies it to a short path, and adds a Start Menu shortcut.
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-Dest <folder>] [-NoLaunch]
param(
    [string]$Dest = "$env:LOCALAPPDATA\Programs\ShowTell",
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

# Prefer a user-folder SDK (dotnet-install.ps1 default), then whatever is on PATH.
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Get-Process ShowTell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Publishing ShowTell to $Dest ..."
& $dotnet publish "$PSScriptRoot\ShowTell.csproj" -c Release -r win-x64 --self-contained true -o $Dest -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $Dest 'ShowTell.exe'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'ShowTell.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $Dest
$lnk.Description = 'Record your screen and voice, then paste it into Claude, Codex or Grok'
$lnk.Save()

$settingsLnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'ShowTell Settings.lnk'))
$settingsLnk.TargetPath = $exe
$settingsLnk.Arguments = '--settings'
$settingsLnk.WorkingDirectory = $Dest
$settingsLnk.Description = 'Change ShowTell shortcut keys, microphone and other settings'
$settingsLnk.Save()

Write-Host "Installed: $exe"
Write-Host "Start Menu shortcuts: ShowTell, ShowTell Settings"
if (-not $NoLaunch) { Start-Process $exe }
