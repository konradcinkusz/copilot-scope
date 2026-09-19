<#
.SYNOPSIS
    CopilotScope installer for Windows.

.DESCRIPTION
    Run it with:

        irm https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.ps1 | iex

    Checks Docker, downloads the compose file and the control script into
    %USERPROFILE%\.copilotscope, starts the stack, waits until the collector
    answers, and offers to point the assistants it finds at it.

    What it does NOT do: generate secrets you then have to keep track of, ask
    you to edit a JSON file by hand, or leave environment variables that only
    exist in one terminal. A local collector binds to 127.0.0.1 and needs no
    key, and assistant configuration is written to the settings file each tool
    reads at startup.

    To pass options, download it first — `irm | iex` has no way to forward them:

        irm https://.../install.ps1 -OutFile install.ps1
        .\install.ps1 -Yes -Capture

.PARAMETER Yes
    Configure every assistant found, without asking.

.PARAMETER NoConnect
    Start the stack only; configure nothing.

.PARAMETER NoStart
    Install the files; start nothing.

.PARAMETER Capture
    Also export prompt/response text (sensitive). Off by default.

.PARAMETER Bind
    Publish beyond loopback. Requires -ApiKey.

.PARAMETER ApiKey
    Ingest key for a shared deployment.

.PARAMETER Tag
    Pin the image tag. Default: latest.

.PARAMETER InstallDir
    Install location. Default: %USERPROFILE%\.copilotscope
#>
[CmdletBinding()]
param(
    [switch] $Yes,
    [switch] $NoConnect,
    [switch] $NoStart,
    [switch] $Capture,
    [string] $Bind,
    [string] $ApiKey,
    [string] $Tag,
    [string] $InstallDir
)

$ErrorActionPreference = 'Stop'

$RepoRaw = if ($env:COPILOTSCOPE_REPO_RAW) { $env:COPILOTSCOPE_REPO_RAW } else { 'https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master' }
if (-not $InstallDir) {
    $InstallDir = if ($env:COPILOTSCOPE_HOME) { $env:COPILOTSCOPE_HOME } else { Join-Path $env:USERPROFILE '.copilotscope' }
}

function Write-Step { param([string] $Text) Write-Host ''; Write-Host $Text -ForegroundColor White }
function Write-Ok   { param([string] $Text) Write-Host "  [ok] $Text" -ForegroundColor Green }
function Write-Warn { param([string] $Text) Write-Host "  [!]  $Text" -ForegroundColor Yellow }
function Write-Info { param([string] $Text) Write-Host "  -    $Text" -ForegroundColor DarkGray }
function Stop-WithError {
    param([string] $Text)
    Write-Host ''
    Write-Host "error: $Text" -ForegroundColor Red
    exit 1
}

# Windows PowerShell's Set-Content -Encoding UTF8 writes a byte-order mark, which
# makes a settings file fail strict JSON parsing. Never use it for these files.
function Write-TextNoBom {
    param([string] $Path, [string] $Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host ''
Write-Host 'CopilotScope — quality scoring for AI coding-assistant sessions.' -ForegroundColor White
Write-Host 'Runs on this machine. Nothing is sent anywhere.' -ForegroundColor DarkGray

# ------------------------------------------------------------- prerequisites

Write-Step 'Checking prerequisites'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Stop-WithError 'Docker Desktop is required — https://docs.docker.com/desktop/install/windows-install/'
}
& docker compose version *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-WithError "this Docker has no 'compose' subcommand. Update Docker Desktop."
}
if (-not $NoStart) {
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'the Docker daemon is not running. Start Docker Desktop and re-run.' }
}
Write-Ok 'docker is available'

# A collector published beyond this machine without a key would serve transcripts
# and accept deletes from anyone who can reach the port. Refuse the combination
# here rather than printing a warning nobody reads.
if ($Bind -and $Bind -ne '127.0.0.1' -and $Bind -ne 'localhost' -and -not $ApiKey) {
    Stop-WithError "-Bind $Bind publishes the collector beyond this machine, so it needs -ApiKey. See SECURITY.md for what the key gates."
}

# ------------------------------------------------------------------ download

Write-Step "Installing into $InstallDir"
New-Item -ItemType Directory -Path (Join-Path $InstallDir 'bin') -Force | Out-Null

function Get-RepoFile {
    param([string] $Path, [string] $Destination)
    try {
        Invoke-WebRequest -Uri "$RepoRaw/$Path" -OutFile "$Destination.part" -UseBasicParsing
    } catch {
        if (Test-Path "$Destination.part") { Remove-Item "$Destination.part" -Force }
        Stop-WithError "could not download $Path from $RepoRaw — $($_.Exception.Message)"
    }
    Move-Item -Path "$Destination.part" -Destination $Destination -Force
}

Get-RepoFile 'docker-compose.ghcr.yml' (Join-Path $InstallDir 'docker-compose.yml')
Write-Ok 'compose file'
$cli = Join-Path (Join-Path $InstallDir 'bin') 'copilotscope.ps1'
Get-RepoFile 'scripts/copilotscope.ps1' $cli
Write-Ok 'control script'

$env:COPILOTSCOPE_HOME = $InstallDir

# ---------------------------------------------------------------------- .env

$envFile = Join-Path $InstallDir '.env'
if (-not (Test-Path $envFile)) { Write-TextNoBom -Path $envFile -Text '' }

function Set-EnvLine {
    param([string] $Name, [string] $Value)
    $lines = @()
    if (Test-Path $envFile) {
        $lines = @(Get-Content $envFile | Where-Object { $_ -notmatch "^$([regex]::Escape($Name))=" })
    }
    $lines += "$Name=$Value"
    Write-TextNoBom -Path $envFile -Text (($lines -join "`n") + "`n")
}

# Claude Code records every session here whether or not telemetry is configured,
# so this is what makes `copilotscope import` work with no client setup at all.
$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$transcripts = Join-Path $claudeDir 'projects'
if (Test-Path $transcripts) {
    # Docker Desktop takes Windows paths in bind mounts, but compose parses the
    # backslash as an escape, so hand it a forward-slash path.
    Set-EnvLine 'CLAUDE_TRANSCRIPTS' ($transcripts -replace '\\', '/')
    Write-Ok "found Claude Code transcripts in $transcripts"
}
if ($Bind) { Set-EnvLine 'COPILOTSCOPE_BIND' $Bind }
if ($ApiKey) { Set-EnvLine 'COPILOTSCOPE_API_KEY' $ApiKey }
if ($Tag) { Set-EnvLine 'COPILOTSCOPE_TAG' $Tag }

# ------------------------------------------------------------ PATH + shim

# A .ps1 is not callable as a bare word from cmd.exe, so ship a .cmd shim beside
# it and put the directory on the user's PATH.
$shim = Join-Path (Join-Path $InstallDir 'bin') 'copilotscope.cmd'
Write-TextNoBom -Path $shim -Text "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0copilotscope.ps1`" %*`r`n"

$binDir = Join-Path $InstallDir 'bin'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$binDir*") {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$binDir", 'User')
    Write-Ok "added $binDir to your PATH (new terminals pick it up)"
} else {
    Write-Ok "$binDir is already on your PATH"
}
$env:Path = "$env:Path;$binDir"

# -------------------------------------------------------------------- start

if (-not $NoStart) {
    Write-Step 'Starting CopilotScope'
    & $cli up
    if ($LASTEXITCODE -ne 0) { Stop-WithError "the stack did not come up. Inspect it with: copilotscope logs collector" }
} else {
    Write-Info 'skipping start (-NoStart). Start it later with: copilotscope up'
}

# ------------------------------------------------------------------ connect

function Confirm-Connect {
    param([string] $Label)
    if ($Yes) { return $true }
    $reply = Read-Host "  Point $Label at CopilotScope? [Y/n]"
    return ($reply -notmatch '^[nN]')
}

if (-not $NoConnect -and -not $NoStart) {
    Write-Step 'Connecting your assistants'
    $found = $false
    $connectArgs = @()
    if ($Capture) { $connectArgs += '-Capture' }

    if ((Test-Path $claudeDir) -or (Get-Command claude -ErrorAction SilentlyContinue)) {
        $found = $true
        if (Confirm-Connect 'Claude Code') {
            Write-Host ''
            & $cli connect claude-code @connectArgs
        } else { Write-Info 'skipped — run later: copilotscope connect claude-code' }
    }

    $vscodeDir = Join-Path (Join-Path $env:APPDATA 'Code') 'User'
    if ((Test-Path $vscodeDir) -or (Get-Command code -ErrorAction SilentlyContinue)) {
        $found = $true
        Write-Host ''
        if (Confirm-Connect 'VS Code Copilot Chat') {
            Write-Host ''
            & $cli connect vscode @connectArgs
        } else { Write-Info 'skipped — run later: copilotscope connect vscode' }
    }

    if (Get-Command copilot -ErrorAction SilentlyContinue) {
        $found = $true
        Write-Host ''
        if (Confirm-Connect 'GitHub Copilot CLI') {
            Write-Host ''
            & $cli connect copilot-cli @connectArgs
        } else { Write-Info 'skipped — run later: copilotscope connect copilot-cli' }
    }

    if (-not $found) {
        Write-Info 'No assistant found on this machine yet.'
        Write-Info 'When you have one: copilotscope connect claude-code | vscode | copilot-cli'
    }
}

# ------------------------------------------------------------- first data

if (-not $NoStart) {
    $transcriptCount = 0
    if (Test-Path $transcripts) {
        $transcriptCount = @(Get-ChildItem -Path $transcripts -Filter '*.jsonl' -Recurse -File -ErrorAction SilentlyContinue).Count
    }

    Write-Step 'Done'
    Write-Ok 'Dashboard    http://localhost:5200'
    Write-Ok 'OTLP ingest  http://localhost:4318'
    Write-Host ''
    if ($transcriptCount -gt 0) {
        Write-Host "  You already have $transcriptCount Claude Code session(s) on disk. Score them now,"
        Write-Host '  with no telemetry configuration at all:'
        Write-Host ''
        Write-Host '      copilotscope import'
        Write-Host ''
    }
    Write-Host '  copilotscope doctor   check the wiring end to end' -ForegroundColor DarkGray
    Write-Host '  copilotscope demo     load demo sessions to look around' -ForegroundColor DarkGray
    Write-Host '  copilotscope probe    verify ingest without an assistant' -ForegroundColor DarkGray
    Write-Host ''
}
