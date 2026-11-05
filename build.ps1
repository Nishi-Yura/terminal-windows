# Builds the self-contained single-file binaries:
#   dist\terminal-windows.exe              (host CLI)
#   dist\guest\TerminalWindowsGuest.exe    (guest helper; copied into the VM by unattended setup)
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Test]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if ($Test) {
    dotnet test (Join-Path $root 'tests\TerminalWindows.Tests') -nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

foreach ($f in @((Join-Path $dist 'terminal-windows.exe'), (Join-Path $dist 'guest\TerminalWindowsGuest.exe'))) {
    if (Test-Path $f) {
        try { [IO.File]::Open($f, 'Open', 'ReadWrite', 'None').Dispose() }
        catch { throw "$f is in use (a running 'terminal-windows' command, MCP server or viewer). Stop it or wait, then rebuild." }
    }
}

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-nologo')
dotnet publish (Join-Path $root 'src\TerminalWindows.Guest') @common -o (Join-Path $dist 'guest')
if ($LASTEXITCODE -ne 0) { throw 'guest publish failed' }
dotnet publish (Join-Path $root 'src\TerminalWindows.Cli') @common -o $dist
if ($LASTEXITCODE -ne 0) { throw 'cli publish failed' }

Get-ChildItem $dist -Filter *.pdb -Recurse | Remove-Item
Write-Host "Built:"
Get-ChildItem $dist -Filter *.exe -Recurse | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
