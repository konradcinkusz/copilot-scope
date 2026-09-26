<#
.SYNOPSIS
    CopilotScope developer setup - the tools to build, test and run CopilotScope from a clone.

.DESCRIPTION
    install.ps1 is for running CopilotScope and installs no .NET, on purpose (ADR-004). This
    script is for working on it. In order, it:

    1. makes sure there is a .NET SDK of the major the code targets, read from the
       TargetFramework so it retargets with the code. One already on the PATH is used as it
       is; otherwise Microsoft's dotnet-install script puts one in
       %LOCALAPPDATA%\Microsoft\dotnet, without admin rights.
    2. installs the Aspire CLI (`aspire run`), a .NET global tool from NuGet, on the Aspire
       major the AppHost is built with. Aspire itself needs no install: the AppHost gets it as
       NuGet packages, and `dotnet run --project src\CopilotScope.AppHost` works without the CLI.
    3. checks for a container runtime, which Aspire runs Postgres and pgAdmin in. It installs
       none: Docker Desktop is licensed on its own terms.
    4. restores the solution, so the Aspire packages are on disk before the first build.
    5. reports the other tools the pull-request checks use: node for the documentation
       checks, shellcheck for the shell scripts. It installs neither.

    Safe to re-run: a step whose tool is already in place does nothing. Run it as
    .\scripts\dev-setup.ps1 and this terminal gets the PATH changes too. On Linux and macOS,
    use scripts/dev-setup.sh.

    ASCII only: Windows PowerShell reads a file without a byte-order mark as the ANSI code
    page, where the bytes of a UTF-8 dash include a quote character.

.PARAMETER DotnetDir
    Use the SDK in this directory, installing it there if it is missing. Default: the SDK on
    the PATH if it is the right major, else %LOCALAPPDATA%\Microsoft\dotnet.

.PARAMETER NoAspireCli
    Skip the Aspire CLI.

.PARAMETER NoRestore
    Skip `dotnet restore`.

.PARAMETER Persist
    Also store DOTNET_ROOT and the PATH entries this needs at User scope, so that new
    terminals find them.

.EXAMPLE
    .\scripts\dev-setup.ps1

.EXAMPLE
    .\scripts\dev-setup.ps1 -Persist
#>
[CmdletBinding()]
param(
    [string] $DotnetDir,
    [switch] $NoAspireCli,
    [switch] $NoRestore,
    [switch] $Persist
)

# Native commands are checked by $LASTEXITCODE. Windows PowerShell turns a redirected stderr
# line into a terminating error under 'Stop', which would end the script on `docker info`.
$ErrorActionPreference = 'Continue'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DotnetInstallUrl = 'https://dot.net/v1/dotnet-install.ps1'
# Where `dotnet tool install --global` puts tools: the SDK honours DOTNET_CLI_HOME over the profile.
$ToolsDir = Join-Path (Join-Path $(if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { $HOME }) '.dotnet') 'tools'
$Sep = [System.IO.Path]::PathSeparator

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

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Stop-WithError 'on Linux and macOS, run scripts/dev-setup.sh.'
}

# The SDK major is the TargetFramework's, and the Aspire CLI's is the AppHost SDK's: read from
# the project files, so a retarget needs no edit here.
$collectorProject = Join-Path $RepoRoot 'src\CopilotScope.Collector\CopilotScope.Collector.csproj'
$appHostProject = Join-Path $RepoRoot 'src\CopilotScope.AppHost\CopilotScope.AppHost.csproj'
if (-not (Test-Path $collectorProject) -or
    -not ((Get-Content -Raw $collectorProject) -match '<TargetFramework>net(\d+\.\d+)</TargetFramework>')) {
    Stop-WithError 'no TargetFramework found in src\CopilotScope.Collector - is this a CopilotScope clone?'
}
$Tfm = $Matches[1]
$SdkMajor = $Tfm.Split('.')[0]
$AspireMajor = $null
if ((Test-Path $appHostProject) -and
    ((Get-Content -Raw $appHostProject) -match 'Name="Aspire\.AppHost\.Sdk" Version="(\d+)\.')) {
    $AspireMajor = $Matches[1]
}

# The newest SDK of the right major that the dotnet at $Exe has, or $null.
function Get-Sdk {
    param([string] $Exe)
    if (-not $Exe) { return $null }
    $sdks = & $Exe --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $found = @($sdks | Where-Object { $_ -match "^$SdkMajor\." })
    if ($found.Count -eq 0) { return $null }
    return ($found[-1] -split ' ')[0]
}

# Directories a new terminal needs on its PATH, and DOTNET_ROOT, for what this set up.
$PathDirs = @()
$DotnetRoot = $null
$OrigPath = @($env:PATH -split [regex]::Escape($Sep) | ForEach-Object { $_.TrimEnd('\') })
function Test-OnPath { param([string] $Dir) return $OrigPath -contains $Dir.TrimEnd('\') }

Write-Host ''
Write-Host "CopilotScope developer setup - .NET $Tfm SDK, Aspire, a container runtime." -ForegroundColor White

# ------------------------------------------------------------------ 1. .NET SDK
Write-Step "1. .NET $Tfm SDK"
$Dotnet = $null
$onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $DotnetDir -and $onPath -and (Get-Sdk $onPath.Source)) {
    $Dotnet = $onPath.Source
    Write-Ok "SDK $(Get-Sdk $Dotnet) on the PATH ($Dotnet)"
} else {
    if (-not $DotnetDir) {
        $DotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
        if ($onPath) {
            $have = (& $onPath.Source --list-sdks 2>$null | ForEach-Object { ($_ -split ' ')[0] }) -join ' '
            Write-Info "the dotnet on the PATH has no $SdkMajor.x SDK: $have"
        }
    }
    New-Item -ItemType Directory -Path $DotnetDir -Force | Out-Null
    $DotnetDir = (Resolve-Path $DotnetDir).Path
    $Dotnet = Join-Path $DotnetDir 'dotnet.exe'
    if ((Test-Path $Dotnet) -and (Get-Sdk $Dotnet)) {
        Write-Ok "SDK $(Get-Sdk $Dotnet) in $DotnetDir"
    } else {
        Write-Info "Installing the .NET $Tfm SDK into $DotnetDir, with Microsoft's $DotnetInstallUrl"
        $installer = Join-Path ([System.IO.Path]::GetTempPath()) "dotnet-install-$PID.ps1"
        try {
            # Windows PowerShell 5.1 may still default to TLS 1.0, which the download refuses.
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri $DotnetInstallUrl -OutFile $installer -ErrorAction Stop
        } catch {
            Stop-WithError "could not download $DotnetInstallUrl - $($_.Exception.Message)"
        }
        try {
            & $installer -Channel $Tfm -InstallDir $DotnetDir -NoPath
        } catch {
            Stop-WithError "dotnet-install failed - $($_.Exception.Message)"
        } finally {
            Remove-Item $installer -ErrorAction SilentlyContinue
        }
        if (-not (Get-Sdk $Dotnet)) { Stop-WithError "dotnet-install finished, but $Dotnet has no $SdkMajor.x SDK." }
        Write-Ok "SDK $(Get-Sdk $Dotnet) installed in $DotnetDir"
    }
    # The rest of this script, and this terminal when run with .\, uses this SDK.
    $env:DOTNET_ROOT = $DotnetDir
    if (-not (Test-OnPath $DotnetDir)) { $env:PATH = "$DotnetDir$Sep$env:PATH" }
    $DotnetRoot = $DotnetDir
    $PathDirs += $DotnetDir
}

# -------------------------------------------------------------- 2. Aspire CLI
Write-Step '2. Aspire'
Write-Info 'Aspire itself arrives as NuGet packages on restore - no workload to install.'
if ($NoAspireCli) {
    Write-Info 'Aspire CLI skipped (-NoAspireCli); `dotnet run --project src\CopilotScope.AppHost` needs none.'
} elseif (-not $AspireMajor) {
    Write-Warn 'no Aspire.AppHost.Sdk version found in the AppHost project; Aspire CLI skipped.'
} else {
    $aspireVersion = $null; $aspireWhere = $null
    $aspire = Get-Command aspire -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($aspire) {
        $aspireVersion = & $aspire.Source --version 2>$null | Select-Object -First 1
        $aspireWhere = $aspire.Source
    } else {
        $tool = & $Dotnet tool list --global 2>$null | Where-Object { $_ -match '^aspire\.cli\s' } | Select-Object -First 1
        if ($tool) { $aspireVersion = ($tool -split '\s+')[1]; $aspireWhere = $ToolsDir }
    }
    if (-not $aspireWhere) {
        Write-Info "Installing the Aspire CLI $AspireMajor.x, a .NET global tool from NuGet"
        Push-Location $RepoRoot
        try { & $Dotnet tool install --global Aspire.Cli --version "$AspireMajor.*" } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) {
            Stop-WithError 'installing the Aspire CLI failed; its output is above. -NoAspireCli skips it.'
        }
        Write-Ok "Aspire CLI installed in $ToolsDir"
    } elseif ("$aspireVersion".Split('.')[0] -eq $AspireMajor) {
        Write-Ok "Aspire CLI $aspireVersion ($aspireWhere)"
    } else {
        # One installed for another project, on another major: left alone, but named.
        $shown = if ($aspireVersion) { $aspireVersion } else { 'of unknown version' }
        Write-Warn "Aspire CLI $shown ($aspireWhere), but the AppHost is on Aspire $AspireMajor."
        Write-Warn "from NuGet, it moves with: dotnet tool update --global Aspire.Cli --version `"$AspireMajor.*`""
    }
    if (-not (Test-OnPath $ToolsDir)) {
        $env:PATH = "$ToolsDir$Sep$env:PATH"
        $PathDirs += $ToolsDir
    }
}

# ------------------------------------------------------ 3. container runtime
Write-Step '3. Container runtime'
if (Get-Command docker -ErrorAction SilentlyContinue) {
    & docker info *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok 'Docker is running'
    } else {
        Write-Warn 'Docker is installed but not answering - start Docker Desktop before running the AppHost.'
    }
} elseif (Get-Command podman -ErrorAction SilentlyContinue) {
    Write-Ok 'Podman - Aspire uses it with ASPIRE_CONTAINER_RUNTIME=podman'
} else {
    Write-Warn 'no Docker. Build, test and the collector without Postgres all work without it;'
    Write-Warn "the AppHost's Postgres and pgAdmin do not: https://docs.docker.com/desktop/install/windows-install/"
}

# --------------------------------------------------------------- 4. restore
Write-Step '4. Restore'
if ($NoRestore) {
    Write-Info 'skipped (-NoRestore)'
} else {
    & $Dotnet restore (Join-Path $RepoRoot 'CopilotScope.sln')
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'dotnet restore failed; its output is above.' }
    Write-Ok 'packages restored'
}

# ------------------------------------------------ 5. the pull-request checks
Write-Step '5. Other tools the pull-request checks use (reported, not installed)'
if (Get-Command node -ErrorAction SilentlyContinue) {
    Write-Ok "node $(& node --version) - npm ci; npm run check:diagrams"
} else {
    Write-Info 'no node: needed only for the documentation checks (npm run check:diagrams)'
}
if (Get-Command shellcheck -ErrorAction SilentlyContinue) {
    Write-Ok 'shellcheck'
} else {
    Write-Info 'no shellcheck: CI lints every shell script with it at -S warning'
}

# ------------------------------------------------------------ the environment
if ($PathDirs.Count -gt 0) {
    Write-Step 'New terminals'
    if ($Persist) {
        if ($DotnetRoot) { [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $DotnetRoot, 'User') }
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $entries = @($userPath -split ';' | Where-Object { $_ })
        foreach ($dir in $PathDirs) {
            if (($entries | ForEach-Object { $_.TrimEnd('\') }) -notcontains $dir.TrimEnd('\')) { $entries = @($dir) + $entries }
        }
        [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
        Write-Ok 'stored at User scope - new terminals will have them'
    } else {
        Write-Info 'new terminals do not have these yet; re-run with -Persist to store them at User scope:'
        if ($DotnetRoot) { Write-Host "      DOTNET_ROOT = $DotnetRoot" }
        foreach ($dir in $PathDirs) { Write-Host "      PATH += $dir" }
    }
}

Write-Step 'Ready'
Write-Host '  dotnet build                                   # the whole solution'
Write-Host '  dotnet test                                    # no Docker, no live collector'
if (-not $NoAspireCli -and $AspireMajor) {
    Write-Host '  aspire run                                     # Postgres, pgAdmin, collector, dashboard'
}
Write-Host '  dotnet run --project src\CopilotScope.AppHost  # the same, without the CLI'
Write-Host ''
# Success, whatever the last native command above (docker info, say) returned.
exit 0
