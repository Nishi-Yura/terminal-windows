# Installs terminal-windows for the current user (no administrator rights needed).
#   - copies dist\ to %LOCALAPPDATA%\Programs\terminal-windows
#   - adds that folder to the user PATH (only if it is not there yet)
#   - optionally installs the Claude Code skill and registers the MCP server
# Usage: powershell -ExecutionPolicy Bypass -File install.ps1 [-Skill] [-Mcp] [-Machine default]
param(
    [switch]$Skill,
    [switch]$Mcp,
    [string]$Machine = 'default'
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'dist'
if (-not (Test-Path (Join-Path $src 'terminal-windows.exe'))) {
    throw "dist\terminal-windows.exe not found. Run build.ps1 first."
}
if (-not (Test-Path (Join-Path $src 'guest\TerminalWindowsGuest.exe'))) {
    throw "dist\guest\TerminalWindowsGuest.exe not found. Run build.ps1 first."
}

$dest = Join-Path $env:LOCALAPPDATA 'Programs\terminal-windows'
New-Item -ItemType Directory -Force (Join-Path $dest 'guest') | Out-Null
Copy-Item (Join-Path $src 'terminal-windows.exe') $dest -Force
Copy-Item (Join-Path $src 'guest\TerminalWindowsGuest.exe') (Join-Path $dest 'guest') -Force
Write-Host "Installed to $dest"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $dest) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $dest) -join ';'), 'User')
    Write-Host "Added $dest to your user PATH (open a new terminal to use 'terminal-windows')."
}

$exe = Join-Path $dest 'terminal-windows.exe'
if ($Skill) { & $exe skills install }
if ($Mcp) {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude mcp add terminal-windows -- $exe mcp $Machine
    } else {
        Write-Host "Claude Code CLI not found. Register manually with:"
        Write-Host "  claude mcp add terminal-windows -- `"$exe`" mcp $Machine"
    }
}
& $exe doctor
