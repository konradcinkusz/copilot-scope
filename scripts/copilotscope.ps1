<#
.SYNOPSIS
    CopilotScope control script for Windows — start the stack, wire up an
    assistant, check the wiring.

.DESCRIPTION
    The PowerShell counterpart of scripts/copilotscope. Installed to
    %USERPROFILE%\.copilotscope\bin by install.ps1, and also runnable straight
    from a clone of the repository.

      copilotscope up                    start (or restart) the local stack
      copilotscope connect claude-code   point Claude Code at it, permanently
      copilotscope connect vscode        point VS Code Copilot Chat at it
      copilotscope connect copilot-cli   point GitHub Copilot CLI at it
      copilotscope import                score the Claude Code history on disk
      copilotscope demo | probe          demo sessions / one real OTLP session
      copilotscope mcp install           let Claude Code read its own scores, read-only
      copilotscope skill install         teach it to read those scores correctly
      copilotscope doctor                diagnose "it runs but no sessions appear"
      copilotscope status | logs | open | url | update | down | uninstall

    Written for Windows PowerShell 5.1 as well as PowerShell 7+, so it avoids
    ConvertFrom-Json -AsHashtable and writes JSON without a byte-order mark: a
    BOM makes a settings file fail strict JSON parsing, which is exactly how an
    "I configured it and nothing happened" report is produced.

.PARAMETER Command
    up, connect, disconnect, import, demo, probe, doctor, status, logs, open,
    url, update, down, uninstall, version.

.PARAMETER Target
    For connect / disconnect: claude-code, vscode, copilot-cli, cowork, all.

.EXAMPLE
    copilotscope up
    copilotscope connect claude-code

.EXAMPLE
    copilotscope connect vscode -Capture
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Command = 'help',

    [Parameter(Position = 1)]
    [string] $Target,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Rest,

    [switch] $Capture,
    [switch] $Traces,
    [switch] $Print,
    [switch] $NoPull,
    [switch] $Purge,
    [string] $Bind,
    [string] $ApiKey,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'

$HomeDir = if ($env:COPILOTSCOPE_HOME) { $env:COPILOTSCOPE_HOME } else { Join-Path $env:USERPROFILE '.copilotscope' }
$RepoRaw = if ($env:COPILOTSCOPE_REPO_RAW) { $env:COPILOTSCOPE_REPO_RAW } else { 'https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master' }

# ------------------------------------------------------------------ output

function Write-Head { param([string] $Text) Write-Host $Text -ForegroundColor White }
function Write-Ok   { param([string] $Text) Write-Host "  [ok] $Text" -ForegroundColor Green }
function Write-Warn { param([string] $Text) Write-Host "  [!]  $Text" -ForegroundColor Yellow }
function Write-Bad  { param([string] $Text) Write-Host "  [x]  $Text" -ForegroundColor Red }
function Write-Info { param([string] $Text) Write-Host "  -    $Text" -ForegroundColor DarkGray }
function Stop-WithError {
    param([string] $Text)
    Write-Host "error: $Text" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- discovery

function Get-ComposeFile {
    if ($env:COPILOTSCOPE_COMPOSE_FILE) { return $env:COPILOTSCOPE_COMPOSE_FILE }
    $installed = Join-Path $HomeDir 'docker-compose.yml'
    if (Test-Path $installed) { return $installed }
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $fromClone = Join-Path $repoRoot 'docker-compose.ghcr.yml'
    if (Test-Path $fromClone) { return $fromClone }
    return $null
}

function Get-EnvFile {
    if ($env:COPILOTSCOPE_ENV_FILE) { return $env:COPILOTSCOPE_ENV_FILE }
    return (Join-Path $HomeDir '.env')
}

function Get-EnvValue {
    param([string] $Name)
    $file = Get-EnvFile
    if (-not (Test-Path $file)) { return $null }
    foreach ($line in Get-Content $file) {
        if ($line -match "^$([regex]::Escape($Name))=(.*)$") { return $Matches[1] }
    }
    return $null
}

function Set-EnvValue {
    param([string] $Name, [string] $Value)
    $file = Get-EnvFile
    $dir = Split-Path -Parent $file
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $lines = @()
    if (Test-Path $file) {
        $lines = Get-Content $file | Where-Object { $_ -notmatch "^$([regex]::Escape($Name))=" }
    }
    $lines += "$Name=$Value"
    Write-TextNoBom -Path $file -Text (($lines -join "`n") + "`n")
}

function Get-Bind {
    $bind = Get-EnvValue 'COPILOTSCOPE_BIND'
    if ([string]::IsNullOrWhiteSpace($bind)) { return '127.0.0.1' }
    return $bind
}

function Get-Endpoint {
    if ($Endpoint) { return $Endpoint.TrimEnd('/') }
    if ($env:COPILOTSCOPE_ENDPOINT) { return $env:COPILOTSCOPE_ENDPOINT.TrimEnd('/') }
    $bind = Get-Bind
    # A collector published on every interface is still addressed as localhost from
    # the machine it runs on; printing 0.0.0.0 as an endpoint would be wrong.
    if ($bind -eq '0.0.0.0' -or $bind -eq '::' -or $bind -eq '127.0.0.1') { return 'http://localhost:4318' }
    return "http://${bind}:4318"
}

function Get-DashboardUrl {
    $bind = Get-Bind
    if ($bind -eq '0.0.0.0' -or $bind -eq '::' -or $bind -eq '127.0.0.1') { return 'http://localhost:5200' }
    return "http://${bind}:5200"
}

function Get-ApiKey {
    if ($ApiKey) { return $ApiKey }
    if ($env:COPILOTSCOPE_API_KEY) { return $env:COPILOTSCOPE_API_KEY }
    return (Get-EnvValue 'COPILOTSCOPE_API_KEY')
}

function Get-ClaudeConfigDir {
    if ($env:CLAUDE_CONFIG_DIR) { return $env:CLAUDE_CONFIG_DIR }
    return (Join-Path $env:USERPROFILE '.claude')
}

function Get-VSCodeSettingsFile {
    $roots = @(
        (Join-Path $env:APPDATA 'Code'),
        (Join-Path $env:APPDATA 'Code - Insiders'),
        (Join-Path $env:APPDATA 'VSCodium')
    )
    foreach ($root in $roots) {
        $candidate = Join-Path (Join-Path $root 'User') 'settings.json'
        if (Test-Path $candidate) { return $candidate }
    }
    foreach ($root in $roots) {
        $userDir = Join-Path $root 'User'
        if (Test-Path $userDir) { return (Join-Path $userDir 'settings.json') }
    }
    return $null
}

function Get-TranscriptsDir {
    $configured = Get-EnvValue 'CLAUDE_TRANSCRIPTS'
    if (-not [string]::IsNullOrWhiteSpace($configured)) { return $configured }
    return (Join-Path (Get-ClaudeConfigDir) 'projects')
}

# ------------------------------------------------------------------- files

# Windows PowerShell's Set-Content -Encoding UTF8 writes a byte-order mark, and a
# BOM makes settings.json fail strict JSON parsing. Always write without one.
function Write-TextNoBom {
    param([string] $Path, [string] $Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Merge-JsonObject {
    param($Base, $Patch)
    foreach ($property in $Patch.PSObject.Properties) {
        $existing = $Base.PSObject.Properties[$property.Name]
        if ($existing -and
            $existing.Value -is [psobject] -and $existing.Value -isnot [array] -and
            $property.Value -is [psobject] -and $property.Value -isnot [array]) {
            Merge-JsonObject -Base $existing.Value -Patch $property.Value
        } else {
            $Base | Add-Member -MemberType NoteProperty -Name $property.Name -Value $property.Value -Force
        }
    }
}

# Returns 'ok', 'unparseable' or 'failed'.
function Merge-JsonFile {
    param([string] $Path, [string] $PatchJson)
    $patch = $PatchJson | ConvertFrom-Json
    $current = $null
    if (Test-Path $Path) {
        $text = Get-Content $Path -Raw
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            try { $current = $text | ConvertFrom-Json } catch { return 'unparseable' }
            if ($current -is [array]) { return 'unparseable' }
        }
        # A settings file truncated by an interrupted write is worse than one that
        # was never touched, so keep a copy beside it.
        try { Copy-Item -Path $Path -Destination "$Path.copilotscope.bak" -Force } catch { }
    }
    if ($null -eq $current) { $current = New-Object psobject }
    Merge-JsonObject -Base $current -Patch $patch
    try {
        Write-TextNoBom -Path $Path -Text (($current | ConvertTo-Json -Depth 20) + "`n")
        return 'ok'
    } catch { return 'failed' }
}

function Remove-JsonKeys {
    param([string] $Path, [string[][]] $KeyPaths)
    if (-not (Test-Path $Path)) { return 'ok' }
    $text = Get-Content $Path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) { return 'ok' }
    try { $document = $text | ConvertFrom-Json } catch { return 'unparseable' }
    if ($document -is [array]) { return 'unparseable' }

    foreach ($keyPath in $KeyPaths) {
        $node = $document
        for ($i = 0; $i -lt $keyPath.Count - 1; $i++) {
            if ($null -eq $node) { break }
            $property = $node.PSObject.Properties[$keyPath[$i]]
            $node = if ($property) { $property.Value } else { $null }
        }
        if ($node) {
            $leaf = $keyPath[$keyPath.Count - 1]
            if ($node.PSObject.Properties[$leaf]) { $node.PSObject.Properties.Remove($leaf) }
        }
    }
    # An env block emptied of our keys is noise in someone's settings file.
    $envProperty = $document.PSObject.Properties['env']
    if ($envProperty -and $envProperty.Value -and
        @($envProperty.Value.PSObject.Properties).Count -eq 0) {
        $document.PSObject.Properties.Remove('env')
    }
    try {
        Write-TextNoBom -Path $Path -Text (($document | ConvertTo-Json -Depth 20) + "`n")
        return 'ok'
    } catch { return 'failed' }
}

# ------------------------------------------------------------------ docker

# Deliberately NOT an advanced function: a declared parameter would make
# PowerShell try to bind tokens like `-d` and `--quiet-pull` as parameters of this
# function instead of passing them to docker. With no param block they land in
# $args verbatim. Callers check $LASTEXITCODE afterwards.
function Invoke-Compose {
    $file = Get-ComposeFile
    if (-not $file) {
        Stop-WithError "no compose file found. Re-run the installer, or run this from a clone of the repository."
    }
    $composeArgs = @('compose', '-p', 'copilotscope', '-f', $file)
    $envFile = Get-EnvFile
    if (Test-Path $envFile) { $composeArgs += @('--env-file', $envFile) }
    & docker @composeArgs @args
}

function Assert-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Stop-WithError "Docker is not installed. See https://docs.docker.com/get-docker/"
    }
    & docker compose version *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError "this Docker has no 'compose' subcommand. Update Docker Desktop."
    }
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError "the Docker daemon is not running. Start Docker Desktop and try again."
    }
}

function Get-Health {
    $headers = @{}
    $key = Get-ApiKey
    if ($key) { $headers['x-api-key'] = $key }
    try {
        return Invoke-RestMethod -Uri "$(Get-Endpoint)/api/health" -TimeoutSec 3 -Headers $headers -ErrorAction Stop
    } catch { return $null }
}

function Wait-Healthy {
    param([int] $TimeoutSeconds = 90)
    Write-Host "Waiting for the collector at $(Get-Endpoint) " -NoNewline
    $waited = 0
    while ($true) {
        if (Get-Health) { Write-Host ' up.'; return $true }
        if ($waited -ge $TimeoutSeconds) {
            Write-Host ''
            Write-Bad "not answering after ${TimeoutSeconds}s."
            Write-Info 'Logs: copilotscope logs collector'
            return $false
        }
        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 2
        $waited += 2
    }
}

# ------------------------------------------------------------- connect: data

function Get-ClaudeEnvPatch {
    $values = [ordered]@{
        CLAUDE_CODE_ENABLE_TELEMETRY = '1'
        OTEL_METRICS_EXPORTER        = 'otlp'
        OTEL_LOGS_EXPORTER           = 'otlp'
        OTEL_EXPORTER_OTLP_PROTOCOL  = 'http/protobuf'
        OTEL_EXPORTER_OTLP_ENDPOINT  = (Get-Endpoint)
        OTEL_METRIC_EXPORT_INTERVAL  = '10000'
        OTEL_LOGS_EXPORT_INTERVAL    = '5000'
        OTEL_RESOURCE_ATTRIBUTES     = 'service.name=claude-code'
    }
    if ($Traces) {
        $values['CLAUDE_CODE_ENHANCED_TELEMETRY_BETA'] = '1'
        $values['OTEL_TRACES_EXPORTER'] = 'otlp'
    }
    if ($Capture) {
        $values['OTEL_LOG_USER_PROMPTS'] = '1'
        $values['OTEL_LOG_ASSISTANT_RESPONSES'] = '1'
        $values['OTEL_LOG_TOOL_DETAILS'] = '1'
    }
    $key = Get-ApiKey
    if ($key) { $values['OTEL_EXPORTER_OTLP_HEADERS'] = "x-api-key=$key" }
    return (@{ env = $values } | ConvertTo-Json -Depth 10)
}

function Get-VSCodePatch {
    $values = [ordered]@{
        'github.copilot.chat.otel.enabled'      = $true
        'github.copilot.chat.otel.otlpEndpoint' = (Get-Endpoint)
        'github.copilot.chat.otel.exporterType' = 'otlp-http'
    }
    if ($Capture) { $values['github.copilot.chat.otel.captureContent'] = $true }
    return ($values | ConvertTo-Json -Depth 10)
}

function Show-Patch {
    param([string] $Json)
    ($Json | ConvertFrom-Json | ConvertTo-Json -Depth 20) -split "`n" | ForEach-Object { Write-Host "      $_" }
}

# ----------------------------------------------------------------- commands

function Invoke-Up {
    if ($Bind -and $Bind -ne '127.0.0.1' -and $Bind -ne 'localhost') {
        $effectiveKey = if ($ApiKey) { $ApiKey } else { Get-ApiKey }
        if (-not $effectiveKey) {
            Stop-WithError @"
-Bind $Bind publishes the collector beyond this machine, so it needs a key.
       Re-run with: copilotscope up -Bind $Bind -ApiKey <secret>
       Without one the collector accepts telemetry, serves transcripts and allows
       deletes to anyone who can reach the port. See SECURITY.md.
"@
        }
    }
    Assert-Docker
    if (-not (Test-Path $HomeDir)) { New-Item -ItemType Directory -Path $HomeDir -Force | Out-Null }
    if ($Bind) { Set-EnvValue 'COPILOTSCOPE_BIND' $Bind }
    if ($ApiKey) { Set-EnvValue 'COPILOTSCOPE_API_KEY' $ApiKey }

    if (-not $NoPull) {
        Write-Host 'Pulling images...'
        Invoke-Compose pull --quiet collector dashboard | Out-Null
    }
    Write-Host 'Starting CopilotScope...'
    Invoke-Compose up -d --remove-orphans collector dashboard postgres
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError 'docker compose failed to start. See the output above.'
    }
    Write-Host ''
    if (-not (Wait-Healthy)) { exit 1 }
    Write-Host ''
    Write-Ok "Dashboard    $(Get-DashboardUrl)"
    Write-Ok "OTLP ingest  $(Get-Endpoint)"
    Write-Host ''
}

function Connect-ClaudeCode {
    $file = Join-Path (Get-ClaudeConfigDir) 'settings.json'
    $patch = Get-ClaudeEnvPatch
    Write-Head 'Claude Code'
    if ($Print) {
        Write-Info "would merge into ${file}:"
        Show-Patch $patch
        return
    }
    switch (Merge-JsonFile -Path $file -PatchJson $patch) {
        'ok' {
            Write-Ok "$file updated."
            Write-Info 'Applies to every terminal, every project, and to Claude Code inside VS Code.'
            Write-Info 'No env vars to set and no "same terminal" rule — start (or restart) claude.'
            if ($Capture) { Write-Warn 'Prompt, response and tool text will be exported.' }
            if ($Traces) { Write-Info 'Beta spans on: adds time-to-first-token, and the schema may still change.' }
        }
        'unparseable' {
            Write-Bad "$file exists but is not valid JSON, so it was left untouched."
            Write-Info 'Claude Code settings must be strict JSON — no comments, no trailing commas.'
            Show-Patch $patch
        }
        default { Write-Bad "could not write $file" }
    }
}

function Connect-VSCode {
    $patch = Get-VSCodePatch
    Write-Head 'VS Code (Copilot Chat)'
    $file = Get-VSCodeSettingsFile
    if (-not $file) {
        Write-Warn 'No VS Code settings file found — is it installed?'
        Write-Info 'Add this to Settings JSON (Ctrl+Shift+P, Preferences: Open User Settings (JSON)):'
        Show-Patch $patch
        return
    }
    if ($Print) {
        Write-Info "would merge into ${file}:"
        Show-Patch $patch
        return
    }
    switch (Merge-JsonFile -Path $file -PatchJson $patch) {
        'ok' { Write-Ok "$file updated." }
        'unparseable' {
            Write-Warn 'left untouched: that file uses comments or trailing commas.'
            Write-Info 'VS Code accepts them; a JSON parser does not. Add this by hand:'
            Show-Patch $patch
        }
        default { Write-Bad "could not write $file" }
    }
    Write-Host ''
    Write-Warn 'Reload the VS Code window once the settings are in place.'
    Write-Info 'Ctrl+Shift+P, Developer: Reload Window — settings are read at extension startup.'
    Write-Info 'Then chat in Agent mode: inline completions alone emit no chat telemetry.'
    $key = Get-ApiKey
    if ($key) {
        Write-Host ''
        Write-Warn 'This deployment has an ingest key, which VS Code can only take from the environment:'
        Write-Info "`$env:OTEL_EXPORTER_OTLP_HEADERS = `"x-api-key=$key`"  — then launch VS Code from that shell."
    }
}

function Connect-CopilotCli {
    Write-Head 'GitHub Copilot CLI'
    $values = [ordered]@{
        COPILOT_OTEL_ENABLED        = 'true'
        COPILOT_OTEL_EXPORTER_TYPE  = 'otlp-http'
        OTEL_EXPORTER_OTLP_ENDPOINT = (Get-Endpoint)
        OTEL_EXPORTER_OTLP_PROTOCOL = 'http/protobuf'
    }
    if ($Capture) { $values['OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT'] = 'true' }
    $key = Get-ApiKey
    if ($key) { $values['OTEL_EXPORTER_OTLP_HEADERS'] = "x-api-key=$key" }

    if ($Print) {
        Write-Info 'would set at User scope:'
        foreach ($name in $values.Keys) { Write-Host "      $name = $($values[$name])" }
        return
    }
    foreach ($name in $values.Keys) {
        Set-Item -Path "Env:$name" -Value $values[$name]
        [Environment]::SetEnvironmentVariable($name, $values[$name], 'User')
    }
    Write-Ok 'Set in this session and at User scope, so new terminals inherit it.'
    Write-Info 'Copilot CLI reads environment variables only — that is why this one is not a settings file.'
    if ($key) { Write-Warn 'The ingest key is now stored in your User environment.' }
    Write-Host ''
    Write-Info 'Note: COPILOT_OTEL_CAPTURE_CONTENT is not a real variable. The CLI follows the OTel'
    Write-Info 'GenAI standard, OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT, which -Capture sets.'
}

function Connect-Cowork {
    Write-Head 'Claude Cowork (Claude desktop app)'
    Write-Info "Cowork is configured in the app's own settings, not by a file this script can write:"
    Write-Host ''
    Write-Host '    Claude Desktop -> organization / Cowork settings -> monitoring'
    Write-Host "    OTLP endpoint:  $(Get-Endpoint)/v1/logs"
    Write-Host '    Protocol:       HTTP'
    Write-Host ''
    Write-Warn 'The full /v1/logs path, not the base endpoint Claude Code takes.'
    Write-Warn 'Restart the app afterwards — configuration is read at session start.'
    Write-Info 'Needs a Team or Enterprise plan, Claude Desktop 1.1.4173+, and org admin access.'
    Write-Info 'Cowork sends log events only: no lines-of-code and no time-to-first-token.'
}

function Invoke-Connect {
    switch ($Target) {
        { $_ -in @('claude-code', 'claude') } { Connect-ClaudeCode }
        { $_ -in @('vscode', 'vs-code', 'code') } { Connect-VSCode }
        { $_ -in @('copilot-cli', 'cli') } { Connect-CopilotCli }
        'cowork' { Connect-Cowork }
        'all' { Connect-ClaudeCode; Write-Host ''; Connect-VSCode }
        default {
            Write-Host 'usage: copilotscope connect <claude-code|vscode|copilot-cli|cowork|all> [-Capture] [-Traces] [-Print]'
            exit 2
        }
    }
}

function Invoke-Disconnect {
    $claudeKeys = @(
        @('env', 'CLAUDE_CODE_ENABLE_TELEMETRY'), @('env', 'CLAUDE_CODE_ENHANCED_TELEMETRY_BETA'),
        @('env', 'OTEL_METRICS_EXPORTER'), @('env', 'OTEL_LOGS_EXPORTER'), @('env', 'OTEL_TRACES_EXPORTER'),
        @('env', 'OTEL_EXPORTER_OTLP_PROTOCOL'), @('env', 'OTEL_EXPORTER_OTLP_ENDPOINT'),
        @('env', 'OTEL_EXPORTER_OTLP_HEADERS'), @('env', 'OTEL_METRIC_EXPORT_INTERVAL'),
        @('env', 'OTEL_LOGS_EXPORT_INTERVAL'), @('env', 'OTEL_RESOURCE_ATTRIBUTES'),
        @('env', 'OTEL_LOG_USER_PROMPTS'), @('env', 'OTEL_LOG_ASSISTANT_RESPONSES'),
        @('env', 'OTEL_LOG_TOOL_DETAILS')
    )
    $vscodeKeys = @(
        @('github.copilot.chat.otel.enabled'), @('github.copilot.chat.otel.otlpEndpoint'),
        @('github.copilot.chat.otel.exporterType'), @('github.copilot.chat.otel.captureContent')
    )
    $all = ($Target -eq 'all' -or [string]::IsNullOrWhiteSpace($Target))

    if ($all -or $Target -in @('claude-code', 'claude')) {
        $file = Join-Path (Get-ClaudeConfigDir) 'settings.json'
        if ((Remove-JsonKeys -Path $file -KeyPaths $claudeKeys) -eq 'ok') {
            Write-Ok "Claude Code telemetry keys removed from $file"
        } else {
            Write-Warn "could not edit $file — remove the env keys by hand"
        }
    }
    if ($all -or $Target -in @('vscode', 'vs-code', 'code')) {
        $file = Get-VSCodeSettingsFile
        if ($file -and (Test-Path $file)) {
            if ((Remove-JsonKeys -Path $file -KeyPaths $vscodeKeys) -eq 'ok') {
                Write-Ok "Copilot OTel settings removed from $file (reload the window)"
            } else {
                Write-Warn "could not edit $file — remove the github.copilot.chat.otel.* keys by hand"
            }
        }
    }
    if ($all -or $Target -in @('copilot-cli', 'cli')) {
        foreach ($name in @('COPILOT_OTEL_ENABLED', 'COPILOT_OTEL_EXPORTER_TYPE',
                'OTEL_EXPORTER_OTLP_ENDPOINT', 'OTEL_EXPORTER_OTLP_PROTOCOL',
                'OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT', 'OTEL_EXPORTER_OTLP_HEADERS')) {
            [Environment]::SetEnvironmentVariable($name, $null, 'User')
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
        Write-Ok 'Copilot CLI variables removed from this session and User scope.'
    }
}

function Invoke-Tool {
    param([string[]] $ToolArgs)
    Assert-Docker
    Invoke-Compose run --rm --quiet-pull tools @ToolArgs
}

function Invoke-Import {
    $transcripts = Get-TranscriptsDir
    if (-not (Test-Path $transcripts)) {
        Write-Bad "no Claude Code transcripts at $transcripts"
        Write-Info 'That directory is where Claude Code records every session. If you use it, run'
        Write-Info 'claude once, or point the importer elsewhere with CLAUDE_TRANSCRIPTS.'
        exit 1
    }
    Set-EnvValue 'CLAUDE_TRANSCRIPTS' $transcripts
    Write-Host "Importing Claude Code transcripts from $transcripts"
    Write-Info 'Prompt text stays out unless you pass --include-content.'
    $passthrough = @('import')
    if ($Rest) { $passthrough += $Rest }
    Assert-Docker
    Invoke-Compose run --rm --quiet-pull tools @passthrough
    Write-Host ''
    Write-Info 'Sessions land badged "imported" with lower confidence: a transcript records tokens,'
    Write-Info 'models, tools and timings, but no latency, edit-decision or feedback signal.'
    Write-Info 'Repository labels come from git, which this container cannot reach — imported sessions'
    Write-Info 'carry no repository unless you run the importer from a clone instead.'
}

function Invoke-Probe {
    $id = "probe-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
    Write-Host 'Sending a probe session through the real OTLP/HTTP path...'
    Assert-Docker
    Invoke-Compose run --rm --quiet-pull tools probe $id
    Start-Sleep -Seconds 2
    $headers = @{}
    $key = Get-ApiKey
    if ($key) { $headers['x-api-key'] = $key }
    try {
        Invoke-RestMethod -Uri "$(Get-Endpoint)/api/sessions/$id" -TimeoutSec 5 -Headers $headers -ErrorAction Stop | Out-Null
        Write-Ok "'$id' is in the collector. Ingest, decoding, scoring and persistence all work."
        Write-Info 'Anything missing after this is client configuration: copilotscope doctor'
    } catch {
        Write-Bad 'the probe session did not appear.'
        Write-Info 'Logs: copilotscope logs collector'
        exit 1
    }
}

function Invoke-Doctor {
    $problems = 0
    Write-Head 'CopilotScope doctor'
    Write-Host ''

    Write-Head 'Stack'
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Bad 'Docker is not installed'; $problems++
    } else {
        & docker info *> $null
        if ($LASTEXITCODE -ne 0) { Write-Bad 'the Docker daemon is not running'; $problems++ }
        else {
            Write-Ok 'docker is running'
            if (Get-ComposeFile) {
                $running = (& docker compose -p copilotscope -f (Get-ComposeFile) ps --services --filter status=running) -join ' '
                if ($running -match 'collector') { Write-Ok "containers up: $running" }
                else { Write-Bad 'the collector container is not running — copilotscope up'; $problems++ }
            } else {
                Write-Warn 'no compose file found, so the stack cannot be inspected from here'
            }
        }
    }

    Write-Host ''
    Write-Head 'Collector'
    $health = Get-Health
    if ($health) {
        Write-Ok "$(Get-Endpoint)/api/health answers"
        Write-Info "environment: $($health.environment), persistence: $($health.persistence), sessions held: $($health.sessions)"
        if (-not $health.persistence) {
            Write-Warn 'running in-memory only: history is lost on restart (no Postgres connection string)'
        }
    } else {
        Write-Bad "$(Get-Endpoint)/api/health does not answer"; $problems++
    }

    $bind = Get-Bind
    $key = Get-ApiKey
    if ($bind -ne '127.0.0.1' -and $bind -ne 'localhost' -and -not $key) {
        Write-Bad "published on $bind with no ingest key: anyone who can reach the port can read"
        Write-Info 'transcripts and delete history. Set COPILOTSCOPE_API_KEY, or bind to 127.0.0.1.'
        $problems++
    } elseif ($key) { Write-Ok 'ingest key configured' }
    else { Write-Ok 'loopback only, no key needed' }

    Write-Host ''
    Write-Head 'Assistants'
    $claudeFile = Join-Path (Get-ClaudeConfigDir) 'settings.json'
    if (Test-Path $claudeFile) {
        $text = Get-Content $claudeFile -Raw
        if ($text -match 'CLAUDE_CODE_ENABLE_TELEMETRY') {
            if ($text -match [regex]::Escape((Get-Endpoint))) { Write-Ok "Claude Code configured in $claudeFile" }
            else { Write-Warn "Claude Code has telemetry on, but points somewhere other than $(Get-Endpoint)"; $problems++ }
            if ($text -notmatch 'OTEL_LOGS_EXPORTER') {
                Write-Bad 'OTEL_LOGS_EXPORTER is missing — the log events are what carry the session'; $problems++
            }
        } else { Write-Info 'Claude Code not configured — copilotscope connect claude-code' }
    } else { Write-Info 'Claude Code not configured — copilotscope connect claude-code' }

    $vsFile = Get-VSCodeSettingsFile
    if ($vsFile -and (Test-Path $vsFile)) {
        if ((Get-Content $vsFile -Raw) -match 'github\.copilot\.chat\.otel\.enabled') {
            Write-Ok "VS Code configured in $vsFile"
            Write-Info 'If sessions still do not appear: reload the window, and chat in Agent mode.'
        } else { Write-Info 'VS Code not configured — copilotscope connect vscode' }
    }

    if ($env:OTEL_EXPORTER_OTLP_ENDPOINT -and $env:OTEL_EXPORTER_OTLP_ENDPOINT -ne (Get-Endpoint)) {
        Write-Warn "OTEL_EXPORTER_OTLP_ENDPOINT is set to $($env:OTEL_EXPORTER_OTLP_ENDPOINT)"
        Write-Info 'An environment variable beats a settings file for anything launched from here.'
        $problems++
    }

    Write-Host ''
    Write-Head 'History on disk'
    $transcripts = Get-TranscriptsDir
    if (Test-Path $transcripts) {
        $count = @(Get-ChildItem -Path $transcripts -Filter '*.jsonl' -Recurse -File -ErrorAction SilentlyContinue).Count
        if ($count -gt 0) {
            Write-Ok "$count Claude Code transcript(s) in $transcripts"
            Write-Info 'Score them without configuring anything: copilotscope import'
        } else { Write-Info "no transcripts in $transcripts yet" }
    } else { Write-Info "$transcripts does not exist (Claude Code has not run, or uses another location)" }

    Write-Host ''
    if ($problems -eq 0) {
        Write-Ok 'no problems found.'
        if ($health -and $health.sessions -eq 0) {
            Write-Host ''
            Write-Info 'No sessions yet. Chat with a configured assistant, or:'
            Write-Info '  copilotscope demo     fabricated sessions, badged demo'
            Write-Info '  copilotscope import   your real Claude Code history'
            Write-Info '  copilotscope probe    one session over the real OTLP path'
        }
        return
    }
    Write-Bad "$problems problem(s) above."
    Write-Info 'Full troubleshooting list: docs/TUTORIAL.md section 9'
    exit 1
}

function Show-McpUsage {
    Write-Host 'usage: copilotscope mcp [install]'
    Write-Host ''
    Write-Host '  (no argument)  run the read-only MCP server on stdin/stdout. An MCP client'
    Write-Host '                 starts this; running it in a terminal just waits for input.'
    Write-Host '  install        register it with Claude Code as "copilotscope".'
    Write-Host ''
    Write-Host '  Tools: health, list_sessions, get_session, overview, signal_coverage'
    Write-Host '  There is no write, delete, seed or import tool, by design.'
}

# The server has to be registered under the name "copilotscope": the collector
# recognises its own reads by tool name (mcp__copilotscope__*) to keep them out of the
# scores, and a server registered under another name would be counted like any other
# tool -- quietly moving the numbers it was asked about.
#
# What gets registered is the docker command, not this script. PowerShell re-emits a
# native command's stdout through its own object pipeline, which is fine for log lines
# and is not a safe carrier for a byte-framed protocol; the client launching docker
# itself takes this script out of the data path entirely.
function Get-McpCommand {
    $file = Get-ComposeFile
    if (-not $file) {
        Stop-WithError "no compose file found. Re-run the installer, or run this from a clone of the repository."
    }
    $dockerArgs = @('compose', '-p', 'copilotscope', '-f', $file)
    $envFile = Get-EnvFile
    if (Test-Path $envFile) { $dockerArgs += @('--env-file', $envFile) }
    # -T: `compose run` allocates a TTY by default, and a TTY echoes what it reads and
    # rewrites line endings, corrupting a transport framed one JSON message per line.
    $dockerArgs += @('run', '--rm', '-T', '--quiet-pull', 'tools', 'mcp')
    return $dockerArgs
}

function Install-Mcp {
    $dockerArgs = Get-McpCommand
    $printable = 'docker ' + ($dockerArgs -join ' ')

    Write-Head 'MCP server'
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Warn 'claude is not on PATH, so nothing was registered.'
        Write-Info 'Register it yourself with:'
        Write-Info "  claude mcp add copilotscope -- $printable"
        return
    }

    & claude mcp add copilotscope -- docker @dockerArgs
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "registered with Claude Code as 'copilotscope'."
        Write-Info 'Read-only: sessions, scores, turn analysis, signal coverage.'
        Write-Info 'Its own calls are excluded from scoring at ingest, so asking about a'
        Write-Info 'score does not change it.'
    }
    else {
        Write-Bad 'claude mcp add failed.'
        Write-Info "Register it by hand with: claude mcp add copilotscope -- $printable"
    }
}

function Invoke-Mcp {
    if ($Target -eq 'install') { Install-Mcp; return }
    if ($Target -eq 'help') { Show-McpUsage; return }

    Assert-Docker
    # -T is load-bearing: `compose run` allocates a TTY by default, and a TTY echoes
    # what it reads and rewrites line endings, which corrupts a transport framed as
    # one JSON message per line.
    Invoke-Compose run --rm -T --quiet-pull tools mcp
}

# The skill text ships inside the tools image so this works without a clone; a clone,
# when there is one, wins so an edit can be tried without rebuilding the image.
function Get-SkillText {
    if ($PSCommandPath) {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
        $local = Join-Path $repoRoot 'skills/copilotscope/SKILL.md'
        if (Test-Path $local) { return [System.IO.File]::ReadAllText($local) }
    }
    Assert-Docker
    return (Invoke-Compose run --rm -T --quiet-pull tools skill | Out-String)
}

function Invoke-Skill {
    $action = if ($Target) { $Target } else { 'install' }

    if ($action -in @('show', 'print')) { Write-Host (Get-SkillText); return }
    if ($action -eq 'help') {
        Write-Host 'usage: copilotscope skill [install|show]'
        Write-Host ''
        Write-Host "  install  write it to $(Join-Path (Get-ClaudeConfigDir) 'skills/copilotscope/SKILL.md')"
        Write-Host '  show     print it and change nothing'
        return
    }
    if ($action -ne 'install') {
        Stop-WithError "unknown skill action '$action' -- try: copilotscope skill install"
    }

    Write-Head 'Claude Code skill'
    $text = Get-SkillText
    # An empty file would install cleanly and teach nothing, which is the failure a
    # user would never notice. Refuse it instead.
    if ([string]::IsNullOrWhiteSpace($text)) {
        Write-Bad 'the skill came back empty; nothing was written.'
        return
    }

    $dest = Join-Path (Get-ClaudeConfigDir) 'skills/copilotscope/SKILL.md'
    Write-TextNoBom $dest $text
    Write-Ok "$dest written."
    Write-Info 'Teaches the assistant to read a score correctly: quote confidence with the'
    Write-Info 'number, check signal coverage before comparing assistants, and never rank'
    Write-Info 'people with it. Also carries the "no sessions appear" triage path.'
    Write-Info 'Restart claude to pick it up.'
}

function Show-Usage {
    Write-Host ''
    Write-Host 'copilotscope — quality scoring for AI coding-assistant sessions, on your machine.'
    Write-Host ''
    Write-Host '  up [-Bind ADDR] [-ApiKey KEY]    start the stack (nothing to declare locally)'
    Write-Host '  connect <claude-code|vscode|copilot-cli|cowork|all> [-Capture] [-Traces]'
    Write-Host '  disconnect <target|all>          undo that'
    Write-Host '  import [--dry-run]               score the Claude Code history already on disk'
    Write-Host '  demo [quick|demo]                load fabricated demo sessions'
    Write-Host '  probe                            send one session over the real OTLP path'
    Write-Host "  mcp [install]                    read-only MCP server over the collector's API"
    Write-Host '  skill [install|show]             the skill that teaches an assistant to read a score'
    Write-Host '  doctor                           diagnose "it runs but no sessions appear"'
    Write-Host '  status | logs | open | url        inspect the running stack'
    Write-Host '  update | down | uninstall [-Purge]'
    Write-Host ''
    Write-Host "  Dashboard $(Get-DashboardUrl)  ·  OTLP ingest $(Get-Endpoint)"
    Write-Host '  Docs https://github.com/konradcinkusz/copilot-scope'
    Write-Host ''
}

switch ($Command.ToLowerInvariant()) {
    'up' { Invoke-Up }
    'connect' { Invoke-Connect }
    'disconnect' { Invoke-Disconnect }
    'import' { Invoke-Import }
    'demo' { Invoke-Tool @('demo', $(if ($Target) { $Target } else { 'quick' })) }
    'seed' { Invoke-Tool @('demo', $(if ($Target) { $Target } else { 'quick' })) }
    'probe' { Invoke-Probe }
    'mcp' { Invoke-Mcp }
    'skill' { Invoke-Skill }
    'doctor' { Invoke-Doctor }
    'status' { Assert-Docker; Invoke-Compose ps | Out-Host; if (Get-Health) { Write-Ok "collector healthy at $(Get-Endpoint)"; Write-Ok "dashboard at $(Get-DashboardUrl)" } else { Write-Warn 'the collector is not answering — copilotscope doctor' } }
    'ps' { Assert-Docker; Invoke-Compose ps | Out-Host }
    'logs' { Assert-Docker; Invoke-Compose logs -f --tail 100 $(if ($Target) { $Target } else { 'collector' }) }
    'open' { Start-Process (Get-DashboardUrl) }
    'url' { Write-Host (Get-DashboardUrl) }
    'update' {
        Assert-Docker
        Write-Host 'Pulling the newest images...'
        Invoke-Compose pull | Out-Null
        Invoke-Compose up -d --remove-orphans collector dashboard postgres | Out-Null
        Wait-Healthy | Out-Null
        Write-Ok 'up to date.'
    }
    'down' { Assert-Docker; Invoke-Compose down | Out-Null; Write-Ok 'stopped. Data stays in the copilotscope-pgdata volume.' }
    'stop' { Assert-Docker; Invoke-Compose down | Out-Null; Write-Ok 'stopped.' }
    'uninstall' {
        Assert-Docker
        if ($Purge) { Invoke-Compose down -v | Out-Null; Write-Ok 'containers and the database volume removed.' }
        else { Invoke-Compose down | Out-Null; Write-Ok 'containers removed. The volume is kept; add -Purge to delete it.' }
        $Target = 'all'
        Invoke-Disconnect
        Write-Host ''
        Write-Info "Remove the control script and its config with: Remove-Item -Recurse $HomeDir"
    }
    'version' { Write-Host 'copilotscope control script v1' }
    default { Show-Usage }
}
