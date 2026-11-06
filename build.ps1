# Builds dist\terminal-windows.exe: one self-contained exe. The guest helper (copied into the VM by
# unattended setup) is published to artifacts\guest first and embedded in it as a resource.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Test]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if ($Test) {
    dotnet test (Join-Path $root 'tests\TerminalWindows.Tests') -nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

foreach ($f in @((Join-Path $dist 'terminal-windows.exe'))) {
    if (Test-Path $f) {
        try { [IO.File]::Open($f, 'Open', 'ReadWrite', 'None').Dispose() }
        catch { throw "$f is in use (a running 'terminal-windows' command, MCP server or viewer). Stop it or wait, then rebuild." }
    }
}

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-nologo')
$guestOut = Join-Path $root 'artifacts\guest'
dotnet publish (Join-Path $root 'src\TerminalWindows.Guest') @common -o $guestOut
if ($LASTEXITCODE -ne 0) { throw 'guest publish failed' }
$guestExe = Join-Path $guestOut 'TerminalWindowsGuest.exe'
# An old dist\guest folder would win over the embedded helper; remove it.
if (Test-Path (Join-Path $dist 'guest')) { Remove-Item -Recurse -Force (Join-Path $dist 'guest') }
dotnet publish (Join-Path $root 'src\TerminalWindows.Cli') @common -o $dist "-p:GuestExePath=$guestExe"
if ($LASTEXITCODE -ne 0) { throw 'cli publish failed' }

Get-ChildItem $dist -Filter *.pdb -Recurse | Remove-Item
Write-Host "Built:"
Get-ChildItem $dist -Filter *.exe -Recurse | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
