# One-line install:
#   irm https://raw.githubusercontent.com/Nishi-Yura/terminal-windows/main/get.ps1 | iex
# For the current user only (no administrator rights):
#   - When a GitHub Release exists: downloads its terminal-windows.exe and terminal-windows.exe.sha256,
#     checks the SHA-256 (a mismatch stops everything), installs to %USERPROFILE%\.terminal-windows\bin
#     and adds that folder to the user PATH. Seconds; nothing to build.
#   - Otherwise (or with -FromSource): builds from source. The .NET 8 SDK goes into
#     %USERPROFILE%\.terminal-windows\sdk from Microsoft's official dotnet-install script (unless a .NET 8+ SDK
#     is installed); then build.ps1.
#   - Then opens terminal-windows (the numbered menu / setup wizard).
# Run it again to update. Options when saved as a file:
#   -Tag v0.1.0        a specific release          -FromSource      build from source even if a release exists
#   -Ref <branch|tag>  source to build             -Source <zip>    a local zip of the repository (source build)
#   -ReleaseDir <dir>  a local folder holding the release files (to check a release before publishing it)
#   -NoStart           do not open terminal-windows at the end
#   -Prefix <dir>      install somewhere else than %USERPROFILE%\.terminal-windows
param([string]$Tag, [switch]$FromSource, [string]$Ref = 'main', [string]$Source, [string]$ReleaseDir, [switch]$NoStart,
      [string]$Prefix = (Join-Path $HOME '.terminal-windows'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is many times slower with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$repo = 'Nishi-Yura/terminal-windows'
$home2 = $Prefix
$bin = Join-Path $home2 'bin'
$exe = Join-Path $bin 'terminal-windows.exe'
New-Item -ItemType Directory -Force $home2 | Out-Null

function Step($t) { Write-Host ''; Write-Host "> $t" -ForegroundColor Cyan }

# Copies the exe into bin. A running copy (an AI app's MCP server, say) cannot be overwritten but can be renamed.
function Install-Exe([string]$file) {
    New-Item -ItemType Directory -Force $bin | Out-Null
    Get-ChildItem $bin -Filter 'terminal-windows.old-*.exe' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    try { Copy-Item $file $exe -Force }
    catch [System.IO.IOException] {
        Rename-Item $exe ("terminal-windows.old-{0:yyyyMMddHHmmss}.exe" -f (Get-Date))
        Copy-Item $file $exe -Force
        Write-Host '  The old version was still running; restart the apps that use it to load the new one.'
    }
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($userPath -split ';' | Where-Object { $_ })
    if ($parts -notcontains $bin) {
        [Environment]::SetEnvironmentVariable('Path', (($parts + $bin) -join ';'), 'User')
        Write-Host "  Added $bin to your user PATH."
    }
}

function Sha256([string]$file) { (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant() }

# ---- release ----
function Install-Release {
    $tmp = Join-Path $env:TEMP ('terminal-windows-release-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $file = Join-Path $tmp 'terminal-windows.exe'
        $sumFile = Join-Path $tmp 'terminal-windows.exe.sha256'
        if ($ReleaseDir) {
            Step "Release files from $ReleaseDir"
            Copy-Item (Join-Path $ReleaseDir 'terminal-windows.exe') $file
            Copy-Item (Join-Path $ReleaseDir 'terminal-windows.exe.sha256') $sumFile
        } else {
            $api = "https://api.github.com/repos/$repo/releases"
            try {
                # /releases/latest skips pre-releases; the list includes them (newest first).
                $rel = if ($Tag) { Invoke-RestMethod "$api/tags/$Tag" } else { Invoke-RestMethod "$api`?per_page=10" | Where-Object { -not $_.draft } | Select-Object -First 1 }
            } catch { $rel = $null }
            $asset = $rel.assets | Where-Object name -eq 'terminal-windows.exe' | Select-Object -First 1
            $sum = $rel.assets | Where-Object name -eq 'terminal-windows.exe.sha256' | Select-Object -First 1
            if (-not $rel -or -not $asset -or -not $sum) { return $false }
            Step "Release $($rel.tag_name)"
            Write-Host ("  Downloading terminal-windows.exe ({0:N0} MB)..." -f ($asset.size / 1MB))
            Invoke-WebRequest $asset.browser_download_url -OutFile $file -UseBasicParsing
            Invoke-WebRequest $sum.browser_download_url -OutFile $sumFile -UseBasicParsing
        }
        $expected = ((Get-Content $sumFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = Sha256 $file
        if ($expected -notmatch '^[0-9a-f]{64}$' -or $actual -ne $expected) {
            throw "SHA-256 check failed (expected $expected, got $actual). Nothing was installed."
        }
        Write-Host "  SHA-256 OK ($actual)"
        Step 'Install'
        Install-Exe $file
        return $true
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}

# ---- source ----
function Install-FromSource {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
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

    Step 'Build (a few minutes the first time)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    Step 'Install'
    Install-Exe (Join-Path $src 'dist\terminal-windows.exe')
}

$sourceWanted = $FromSource -or $Source -or ($PSBoundParameters.ContainsKey('Ref'))
if ($sourceWanted -or -not (Install-Release)) {
    if (-not $sourceWanted) { Write-Host 'No release found; building from source.' }
    Install-FromSource
}
if (-not (Test-Path $exe)) { throw 'install failed' }
Write-Host ''
Write-Host "Done: $exe" -ForegroundColor Green
Write-Host 'New terminals know the command "terminal-windows". Run this line again to update.'
if (-not $NoStart) { & $exe }
