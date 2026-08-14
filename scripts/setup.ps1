<#
.SYNOPSIS
    CopilotScope one-command setup wizard.

.DESCRIPTION
    Starts the collector stack, prints ready-to-paste client config (with the
    real endpoint/key filled in), optionally sets CLI env vars in this
    PowerShell session, and runs a TelemetryGen smoke test to confirm the
    pipeline works end to end.

.PARAMETER Mode
    How to start CopilotScope: compose (default), aspire, or skip-start
    (assume it's already running at -Endpoint).

.PARAMETER CopilotCli
    Also configure GitHub Copilot CLI OTel env vars in this session
    (delegates to Enable-CopilotOtel.ps1).

.PARAMETER ClaudeCode
    Also configure Claude Code OTel env vars in this session
    (delegates to Enable-ClaudeCodeOtel.ps1).

.PARAMETER CaptureContent
    Also request prompt/response content capture (sensitive!). Default: off.

.PARAMETER Traces
    Claude Code only: also export the beta trace spans, the one source of
    time-to-first-token for Claude sessions. Beta — the schema may change.

.PARAMETER Endpoint
    OTLP endpoint of the CopilotScope collector. Default: http://localhost:4318

.PARAMETER ApiKey
    x-api-key for ingest auth. In -Mode compose a random key is generated into the
    gitignored .env (reused on re-runs); otherwise unset.

.PARAMETER Persist
    Also store the CLI env vars at User scope (via Enable-CopilotOtel.ps1 /
    Enable-ClaudeCodeOtel.ps1 -Persist) so new terminals pick them up without
    re-running this script. Requires -CopilotCli and/or -ClaudeCode.

.PARAMETER SkipVerify
    Skip the TelemetryGen smoke test.

.PARAMETER HealthTimeoutSeconds
    How long to wait for the collector to come up. Default: 60.

.EXAMPLE
    .\setup.ps1

.EXAMPLE
    .\setup.ps1 -CopilotCli -CaptureContent

.EXAMPLE
    .\setup.ps1 -CopilotCli -Persist

.EXAMPLE
    .\setup.ps1 -Mode aspire -SkipVerify

.EXAMPLE
    .\setup.ps1 -Mode skip-start -Endpoint https://copilotscope.example.com -ApiKey $env:SCOPE_KEY
#>
[CmdletBinding()]
param(
    [ValidateSet('compose', 'aspire', 'skip-start')]
    [string] $Mode = 'compose',
    [switch] $CopilotCli,
    [switch] $ClaudeCode,
    [switch] $CaptureContent,
    [switch] $Traces,
    [string] $Endpoint = 'http://localhost:4318',
    [string] $ApiKey,
    [switch] $Persist,
    [switch] $SkipVerify,
    [int] $HealthTimeoutSeconds = 60
)

if ($Persist -and -not $CopilotCli -and -not $ClaudeCode) {
    Write-Warning "-Persist has no effect without -CopilotCli and/or -ClaudeCode."
}

$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($Mode -eq 'compose') {
    # Compose requires COPILOTSCOPE_API_KEY and POSTGRES_PASSWORD (no default).
    # Reuse an existing .env so re-runs stay stable; otherwise generate secrets
    # into the gitignored .env that docker compose reads.
    $EnvFile = Join-Path $RepoRoot '.env'
    function New-ScopeSecret { -join ((1..48) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) }) }
    $existing = @{}
    if (Test-Path $EnvFile) {
        foreach ($line in Get-Content $EnvFile) {
            if ($line -match '^(?<k>[^=]+)=(?<v>.*)$') { $existing[$Matches.k] = $Matches.v }
        }
    }
    $key = if ($existing.COPILOTSCOPE_API_KEY) { $existing.COPILOTSCOPE_API_KEY } else { New-ScopeSecret }
    $pw  = if ($existing.POSTGRES_PASSWORD)    { $existing.POSTGRES_PASSWORD }    else { New-ScopeSecret }
    "COPILOTSCOPE_API_KEY=$key`nPOSTGRES_PASSWORD=$pw`n" | Set-Content -Path $EnvFile -NoNewline
    Write-Host "Wrote generated secrets to $EnvFile (gitignored)."
    if (-not $ApiKey) { $ApiKey = $key }
}

Write-Host "=== CopilotScope setup ===" -ForegroundColor Cyan
Write-Host ""

# ------------------------------------------------------------- 1. start stack
switch ($Mode) {
    'compose' {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Write-Error "docker is required for -Mode compose."; return
        }
        Write-Host "Starting CopilotScope via Docker Compose..."
        docker compose -f (Join-Path $RepoRoot 'docker-compose.yml') up --build -d
        if ($LASTEXITCODE -ne 0) { Write-Error "docker compose failed to start."; return }
    }
    'aspire' {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            Write-Error "dotnet SDK is required for -Mode aspire."; return
        }
        $log = Join-Path ([System.IO.Path]::GetTempPath()) "copilotscope-aspire-$PID.log"
        Write-Host "Starting CopilotScope via .NET Aspire (background, log: $log)..."
        $appHost = Join-Path $RepoRoot 'src\CopilotScope.AppHost'
        Start-Process -FilePath 'dotnet' -ArgumentList "run --project `"$appHost`"" `
            -RedirectStandardOutput $log -RedirectStandardError $log -WindowStyle Hidden
    }
    'skip-start' {
        Write-Host "Skipping start — assuming CopilotScope is already running at $Endpoint" -ForegroundColor Yellow
    }
}
Write-Host ""

# ------------------------------------------------------------ 2. wait healthy
Write-Host -NoNewline "Waiting for the collector at $Endpoint "
$waited = 0
$healthy = $false
while (-not $healthy) {
    try {
        Invoke-RestMethod -Uri "$Endpoint/api/health" -TimeoutSec 3 -ErrorAction Stop | Out-Null
        $healthy = $true
    } catch {
        if ($waited -ge $HealthTimeoutSeconds) {
            Write-Host ""
            Write-Error "Timed out after ${HealthTimeoutSeconds}s waiting for $Endpoint/api/health. See docs/TUTORIAL.md section 8."
            return
        }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 2
        $waited += 2
    }
}
Write-Host " up."
Invoke-RestMethod -Uri "$Endpoint/api/health" | ConvertTo-Json -Compress | Write-Host
Write-Host ""

# --------------------------------------------------- 3. configure CLI clients
if ($CopilotCli) {
    $cliArgs = @('-Endpoint', $Endpoint)
    if ($CaptureContent) { $cliArgs += '-CaptureContent' }
    if ($ApiKey) { $cliArgs += @('-ApiKey', $ApiKey) }
    if ($Persist) { $cliArgs += '-Persist' }
    & (Join-Path $PSScriptRoot 'Enable-CopilotOtel.ps1') @cliArgs
    Write-Host ""
}

if ($ClaudeCode) {
    $claudeArgs = @('-Endpoint', $Endpoint)
    if ($CaptureContent) { $claudeArgs += '-CaptureContent' }
    if ($Traces) { $claudeArgs += '-Traces' }
    if ($ApiKey) { $claudeArgs += @('-ApiKey', $ApiKey) }
    if ($Persist) { $claudeArgs += '-Persist' }
    & (Join-Path $PSScriptRoot 'Enable-ClaudeCodeOtel.ps1') @claudeArgs
    Write-Host ""
}

# --------------------------------------------------------- 4. VS Code snippet
Write-Host 'VS Code — Settings JSON (Ctrl+Shift+P -> "Preferences: Open User Settings (JSON)"):'
Write-Host "{"
Write-Host "  ""github.copilot.chat.otel.enabled"": true,"
Write-Host "  ""github.copilot.chat.otel.otlpEndpoint"": ""$Endpoint"","
if ($CaptureContent) {
    Write-Host "  ""github.copilot.chat.otel.exporterType"": ""otlp-http"","
    Write-Host "  ""github.copilot.chat.otel.captureContent"": true"
} else {
    Write-Host "  ""github.copilot.chat.otel.exporterType"": ""otlp-http"""
}
Write-Host "}"
Write-Host 'Then: Ctrl+Shift+P -> "Developer: Reload Window" (required — settings are read at startup).'
if ($ApiKey) {
    Write-Host ""
    Write-Host "This endpoint requires an API key. Export before launching VS Code from a terminal:"
    Write-Host "  `$env:OTEL_EXPORTER_OTLP_HEADERS = ""x-api-key=$ApiKey"""
}
Write-Host ""

# --------------------------------------------------------------- 5. smoke test
if (-not $SkipVerify) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host "dotnet not found — skipping smoke test. Open the dashboard and chat with a Copilot"
        Write-Host "client to verify manually, or install the .NET 8 SDK and re-run without -SkipVerify."
    } else {
        $probeId = "setup-probe-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
        $probeLog = Join-Path ([System.IO.Path]::GetTempPath()) "copilotscope-telemetrygen-$PID.log"
        Write-Host "Running smoke test (TelemetryGen) — conversation '$probeId'..."
        $genProject = Join-Path $RepoRoot 'tools\CopilotScope.TelemetryGen'
        $prevKey = $env:COPILOTSCOPE_API_KEY
        $env:COPILOTSCOPE_API_KEY = $ApiKey
        try {
            dotnet run --project $genProject -- $Endpoint $probeId *> $probeLog
        } finally {
            $env:COPILOTSCOPE_API_KEY = $prevKey
        }

        $vwaited = 0
        $found = $false
        while (-not $found -and $vwaited -lt 15) {
            try {
                Invoke-RestMethod -Uri "$Endpoint/api/sessions/$probeId" -TimeoutSec 3 -ErrorAction Stop | Out-Null
                $found = $true
            } catch {
                Start-Sleep -Seconds 1
                $vwaited += 1
            }
        }
        if ($found) {
            Write-Host "Smoke test OK — '$probeId' is visible via the collector API. CopilotScope is healthy end to end."
        } else {
            Write-Warning "Probe session did not appear within 15s. See $probeLog and docs/TUTORIAL.md section 8."
        }
    }
    Write-Host ""
}

Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Dashboard: for -Mode compose, http://localhost:5200 ; for -Mode aspire, see the Aspire dashboard URL above."
