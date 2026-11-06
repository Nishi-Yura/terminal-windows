# Installs terminal-windows for the current user (no administrator rights needed).
#   - copies dist\ to %USERPROFILE%\.terminal-windows\bin (not AppData: packaged apps such as the
#     Claude desktop app see a virtualized AppData, so an exe installed there may be invisible elsewhere)
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

$dest = Join-Path $HOME '.terminal-windows\bin'
New-Item -ItemType Directory -Force $dest | Out-Null
$target = Join-Path $dest 'terminal-windows.exe'
# Leftovers of earlier updates (see below); they are free once their processes have exited.
Get-ChildItem $dest -Filter 'terminal-windows.old-*.exe' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
try {
    Copy-Item (Join-Path $src 'terminal-windows.exe') $target -Force
} catch [System.IO.IOException] {
    # The installed exe is running (e.g. as an MCP server of an open AI app). Windows cannot overwrite a
    # running exe but can rename it: move it aside, then copy. Running copies keep working until restarted.
    Rename-Item $target ("terminal-windows.old-{0:yyyyMMddHHmmss}.exe" -f (Get-Date))
    Copy-Item (Join-Path $src 'terminal-windows.exe') $target -Force
    Write-Host "The old version was still running; restart apps that use it (for example Claude Code) to load the new one."
}
# The guest helper is embedded in terminal-windows.exe; an old guest\ copy would shadow it.
if (Test-Path (Join-Path $dest 'guest')) { Remove-Item -Recurse -Force (Join-Path $dest 'guest') }
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
        & claude mcp add --scope user terminal-windows -- $exe mcp $Machine
    } else {
        Write-Host "Claude Code CLI not found. Register manually with:"
        Write-Host "  claude mcp add --scope user terminal-windows -- `"$exe`" mcp $Machine"
    }
}
& $exe doctor
