<#
.SYNOPSIS
    Loads the OpenAI API key from the (git-ignored) .env file and writes it into
    the API project's user-secrets, so it never touches a tracked config file.

.DESCRIPTION
    Reads OPENAI_API_KEY from .env at the repo root and runs `dotnet user-secrets set`
    for both OpenAI seams the ingestion pipeline uses:
        OpenAIEmbeddings:ApiKey   (embedding generation -> vector(3072) column)
        OpenAIChat:ApiKey         (chunker / mapper agents)
    The two sections share one OpenAI account key. Model names stay in
    appsettings.Development.json; only the secret lives in user-secrets.

.EXAMPLE
    ./set-openai-secrets.ps1
    ./set-openai-secrets.ps1 -EnvFile .env -EnvKey OPENAI_API_KEY
#>
[CmdletBinding()]
param(
    [string]  $EnvFile = (Join-Path $PSScriptRoot '.env'),
    [string]  $EnvKey  = 'OPENAI_API_KEY',
    [string]  $Project = 'src/MedicalAssistance.Ingestion.Api',
    [string[]]$SecretKeys = @('OpenAIEmbeddings:ApiKey', 'OpenAIChat:ApiKey')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $EnvFile)) {
    throw "Env file not found: $EnvFile"
}

# Parse .env: skip blanks and comments, split on the first '=', strip optional quotes.
$apiKey = $null
foreach ($line in Get-Content -LiteralPath $EnvFile) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
    $idx = $trimmed.IndexOf('=')
    if ($idx -lt 1) { continue }
    $name  = $trimmed.Substring(0, $idx).Trim()
    if ($name -ne $EnvKey) { continue }
    $value = $trimmed.Substring($idx + 1).Trim().Trim('"').Trim("'")
    $apiKey = $value
    break
}

if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "'$EnvKey' not found (or empty) in $EnvFile."
}

$projPath = Join-Path $PSScriptRoot $Project
foreach ($secret in $SecretKeys) {
    Write-Host "==> Setting user-secret '$secret'" -ForegroundColor Cyan
    dotnet user-secrets set $secret $apiKey --project $projPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet user-secrets set '$secret' failed (exit $LASTEXITCODE)." }
}

Write-Host "==> Done. $($SecretKeys.Count) secret(s) set from $EnvKey. The key was not printed." -ForegroundColor Green
