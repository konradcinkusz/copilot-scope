<#
.SYNOPSIS
    CopilotScope installer for Windows.

.DESCRIPTION
    Run it with:

        irm https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.ps1 | iex

    Installs the native copilotscope.exe (ADR-004): one self-contained program, with no
    Docker, no .NET and no python or node to install. In order: it picks the release archive
    for this machine, checks it against the release's SHA256SUMS and refuses it on any
    mismatch, installs it into %USERPROFILE%\.copilotscope\app, puts it on your PATH, and
    offers to point the assistants it finds at it, showing each change first and making it
    only on a yes. Then `copilotscope` starts it.

    For a team or a shared server, -Docker installs the Docker Compose stack instead, as
    before.

    To pass options, download it first; `irm | iex` has no way to forward them:

        irm https://.../install.ps1 -OutFile install.ps1
        .\install.ps1 -Yes -Capture

.PARAMETER Yes
    Configure every assistant found, without asking.

.PARAMETER NoConnect
    Install only; configure nothing.

.PARAMETER Capture
    Also export prompt/response text (sensitive). Off by default.

.PARAMETER Version
    A specific release, e.g. v1.1.0. Default: the latest.

.PARAMETER InstallDir
    Install location. Default: %USERPROFILE%\.copilotscope

.PARAMETER Docker
    Install the Docker Compose stack instead of the native binary.

.PARAMETER NoStart
    With -Docker: install the files; start nothing.

.PARAMETER Bind
    With -Docker: publish beyond loopback. Requires -ApiKey.

.PARAMETER ApiKey
    With -Docker: ingest key for a shared deployment.

.PARAMETER Tag
    With -Docker: pin the image tag. Default: latest.
#>
[CmdletBinding()]
param(
    [switch] $Docker,
    [string] $Version,
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

$Repo = 'konradcinkusz/copilot-scope'
$RepoRaw = if ($env:COPILOTSCOPE_REPO_RAW) { $env:COPILOTSCOPE_REPO_RAW } else { "https://raw.githubusercontent.com/$Repo/master" }
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

# ------------------------------------------------------------------ native

function Get-Rid {
    $arch = $null
    try { $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() } catch { }
    if (-not $arch) {
        # Older Windows PowerShell: an x64 shell on ARM64 reports its own architecture in
        # PROCESSOR_ARCHITECTURE and the machine's in PROCESSOR_ARCHITEW6432.
        $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    }
    switch -Regex ($arch) {
        '^(X64|AMD64)$' { return 'win-x64' }
        '^(Arm64|ARM64)$' { return 'win-arm64' }
        default { Stop-WithError "there is no native build for $arch. Use -Docker." }
    }
}

function Install-Native {
    Write-Step 'Finding the build for this machine'
    $rid = Get-Rid
    Write-Ok $rid

    # COPILOTSCOPE_DOWNLOAD_BASE stands in for the release, for a mirror or a test.
    $base = $env:COPILOTSCOPE_DOWNLOAD_BASE
    if (-not $base) {
        $base = if ($Version) { "https://github.com/$Repo/releases/download/$Version" } else { "https://github.com/$Repo/releases/latest/download" }
    }
    $archive = "copilotscope-$rid.zip"

    Write-Step "Downloading $archive"
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("copilotscope-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        try {
            Invoke-WebRequest -Uri "$base/$archive" -OutFile (Join-Path $work $archive) -UseBasicParsing
        } catch {
            # Releases carry native builds from the first one after ADR-004; before that, the
            # Docker stack is what there is.
            if (-not $Version -and -not $env:COPILOTSCOPE_DOWNLOAD_BASE -and (Get-Command docker -ErrorAction SilentlyContinue)) {
                Write-Warn 'the latest release has no native build yet; installing the Docker stack instead.'
                Install-Docker
                return
            }
            Stop-WithError "could not download $base/$archive. Pick a release with native builds (-Version), or install the Docker stack (-Docker)."
        }
        try {
            Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $work 'SHA256SUMS') -UseBasicParsing
        } catch {
            Stop-WithError 'the release has no SHA256SUMS, and a download that cannot be checked is not installed.'
        }
        $expected = $null
        foreach ($line in Get-Content (Join-Path $work 'SHA256SUMS')) {
            $parts = $line -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $archive) { $expected = $parts[0].ToLowerInvariant() }
        }
        if (-not $expected) { Stop-WithError "SHA256SUMS does not list $archive, so it cannot be checked. Nothing was installed." }
        $actual = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $work $archive)).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            Stop-WithError "$archive does not match its checksum (expected $expected, got $actual). Nothing was installed."
        }
        Write-Ok 'checksum verified'

        Write-Step "Installing into $InstallDir"
        Expand-Archive -Path (Join-Path $work $archive) -DestinationPath $work -Force
        $extracted = Join-Path $work "copilotscope-$rid"
        if (-not (Test-Path (Join-Path $extracted 'copilotscope.exe'))) { Stop-WithError 'the archive holds no copilotscope.exe.' }
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        $app = Join-Path $InstallDir 'app'
        $exe = Join-Path $app 'copilotscope.exe'

        # A running copy is stopped first: Windows will not replace a program that is running.
        $restarted = $false
        if (Test-Path $exe) {
            & $exe status *> $null
            if ($LASTEXITCODE -eq 0) {
                & $exe stop *> $null
                $restarted = $true
                Write-Info 'stopped the running CopilotScope to replace it'
            }
        }
        # Swapped in whole: an interrupted install leaves the previous version, never a mixture.
        foreach ($stale in @("$app.new", "$app.old")) { if (Test-Path $stale) { Remove-Item -Recurse -Force $stale } }
        Move-Item -Path $extracted -Destination "$app.new"
        if (Test-Path $app) { Move-Item -Path $app -Destination "$app.old" }
        Move-Item -Path "$app.new" -Destination $app
        if (Test-Path "$app.old") { Remove-Item -Recurse -Force "$app.old" }
        Write-Ok (& $exe version)

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $app) {
            $newPath = if ([string]::IsNullOrEmpty($userPath)) { $app } else { "$userPath;$app" }
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
            Write-Ok "added $app to your PATH (new terminals pick it up)"
        } else {
            Write-Ok "$app is already on your PATH"
        }
        if (($env:Path -split ';') -notcontains $app) { $env:Path = "$env:Path;$app" }

        # The Docker stack on the same port would make copilotscope refuse to start; say so now.
        try {
            $health = Invoke-WebRequest -Uri 'http://127.0.0.1:4318/api/health' -UseBasicParsing -TimeoutSec 2
            if ($health.Content -match 'hostlessSignals') {
                Write-Warn 'a CopilotScope collector already answers on port 4318, most likely the Docker stack.'
                Write-Info "Stop it before starting this one: copilotscope.cmd down from $InstallDir\bin (its data volume is kept)."
            }
        } catch { }

        if (-not $NoConnect) {
            Write-Step 'Connecting your assistants'
            $setupArgs = @('setup')
            if ($Yes) { $setupArgs += '--yes' }
            if ($Capture) { $setupArgs += '--capture' }
            & $exe @setupArgs
        }

        $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
        $transcriptCount = 0
        if (Test-Path (Join-Path $claudeDir 'projects')) {
            $transcriptCount = @(Get-ChildItem -Path (Join-Path $claudeDir 'projects') -Filter '*.jsonl' -Recurse -File -ErrorAction SilentlyContinue).Count
        }

        Write-Step 'Done'
        if ($restarted) { Write-Host '  It was running, and was stopped for the update. Start it again with:' }
        else { Write-Host '  Start it with:' }
        Write-Host ''
        Write-Host '      copilotscope'
        Write-Host ''
        Write-Host '  The dashboard opens at http://localhost:5200; Ctrl+C stops it.'
        if ($transcriptCount -gt 0) { Write-Host "  Your $transcriptCount Claude Code session(s) on disk are scored as it starts." }
        Write-Host ''
        Write-Host '  copilotscope setup    point your assistants at it' -ForegroundColor DarkGray
        Write-Host '  copilotscope doctor   check the wiring end to end' -ForegroundColor DarkGray
        Write-Host ''
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ docker

function Install-Docker {
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
}

# The Docker stack's options mean nothing to the native binary, which binds to loopback only
# and is started by hand; saying so beats quietly ignoring them.
if (-not $Docker -and ($Bind -or $ApiKey -or $Tag)) {
    Stop-WithError '-Bind, -ApiKey and -Tag are for the Docker stack: add -Docker.'
}

if ($Docker) { Install-Docker } else { Install-Native }
