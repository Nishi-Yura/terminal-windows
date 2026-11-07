# One-line install from source (no GitHub Release needed):
#   irm https://raw.githubusercontent.com/Nishi-Yura/terminal-windows/main/get.ps1 | iex
# What it does, for the current user only (no administrator rights):
#   1. the .NET 8 SDK into %USERPROFILE%\.terminal-windows\sdk, from Microsoft's official dotnet-install script,
#      unless a .NET 8+ SDK is already installed
#   2. the source of this repository (a zip from GitHub) into %USERPROFILE%\.terminal-windows\src
#   3. build.ps1, then install.ps1 (exe into %USERPROFILE%\.terminal-windows\bin, added to the user PATH)
#   4. opens terminal-windows (the numbered menu / setup wizard)
# Run it again to update. Options when saved as a file: -Ref <branch or tag>, -NoStart,
# -Source <zip of the repository> (use a local copy instead of downloading it).
param([string]$Ref = 'main', [switch]$NoStart, [string]$Source)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is many times slower with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$repo = 'Nishi-Yura/terminal-windows'
$home2 = Join-Path $HOME '.terminal-windows'
New-Item -ItemType Directory -Force $home2 | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Step($t) { Write-Host ''; Write-Host "> $t" -ForegroundColor Cyan }

# 1. .NET SDK
Step '.NET 8 SDK'
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) {
    $sdks = & $cmd.Source --list-sdks 2>$null
    if ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }) { $dotnet = $cmd.Source }
}
$sdkDir = Join-Path $home2 'sdk'
if (-not $dotnet -and (Test-Path (Join-Path $sdkDir 'dotnet.exe'))) { $dotnet = Join-Path $sdkDir 'dotnet.exe' }
if (-not $dotnet) {
    Write-Host "  Installing into $sdkDir (about 250 MB, from Microsoft)..."
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -InstallDir $sdkDir -NoPath
    if ($LASTEXITCODE -ne 0) { throw '.NET SDK install failed' }
    $dotnet = Join-Path $sdkDir 'dotnet.exe'
}
$env:PATH = (Split-Path $dotnet) + ';' + $env:PATH
$env:DOTNET_ROOT = Split-Path $dotnet
Write-Host "  $dotnet ($(& $dotnet --version))"

# 2. Source
$zip = Join-Path $env:TEMP 'terminal-windows-src.zip'
if ($Source) {
    Step "Source ($Source)"
    Copy-Item $Source $zip -Force
} else {
    Step "Source ($repo@$Ref)"
    $kind = if ($Ref -match '^v\d') { 'tags' } else { 'heads' }
    Invoke-WebRequest "https://github.com/$repo/archive/refs/$kind/$Ref.zip" -OutFile $zip -UseBasicParsing
}
$src = Join-Path $home2 'src'
$tmp = Join-Path $home2 'src.new'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive $zip $tmp
Remove-Item $zip
$inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
Move-Item $inner.FullName $src
Remove-Item -Recurse -Force $tmp
Write-Host "  $src"

# 3. Build and install
Step 'Build (a few minutes the first time)'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Step 'Install'
# (install.ps1 ends with 'doctor', whose exit code reports missing VirtualBox etc.; the wizard handles those.)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'install.ps1')
$exe = Join-Path $home2 'bin\terminal-windows.exe'
if (-not (Test-Path $exe)) { throw 'install failed' }
Write-Host ''
Write-Host "Done: $exe" -ForegroundColor Green
Write-Host 'New terminals know the command "terminal-windows". Run this line again to update.'
if (-not $NoStart) { & $exe }
