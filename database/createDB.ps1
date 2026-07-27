<#
.SYNOPSIS
    Drops and recreates the ai_med database, then applies every EF Core migration
    in order so you get a clean, fully-migrated schema ready for ingestion.

.DESCRIPTION
    The dev database is a pgvector Postgres (image pgvector/pgvector:pg17) exposed
    on localhost:5433 by the `ai-med-postgres` container. This script:
      1. Terminates open connections and DROP/CREATEs the target database (clean slate).
      2. Runs `dotnet ef database update`, which applies all migrations sequentially.

    A plain `dotnet ef database update` alone is NOT idempotent here: the seed
    migrations (agent instructions, chunkers) INSERT fixed rows, so re-running them
    against a database that already has those rows fails on a duplicate-key violation.
    Dropping first avoids that.

.EXAMPLE
    ./database/createDB.ps1
    # Uses the defaults below (localhost:5433, database ai_med, postgres/postgres).

.NOTES
    Requires: the ai-med-postgres container running (docker start ai-med-postgres)
    and a psql client on PATH (falls back to the PostgreSQL 15 install if present).
#>
[CmdletBinding()]
param(
    [string]$DbHost   = 'localhost',
    [int]   $Port     = 5433,
    [string]$Database = 'ai_med',
    [string]$Username = 'postgres',
    [string]$Password = 'postgres'
)

$ErrorActionPreference = 'Stop'

# --- locate psql -----------------------------------------------------------
$psql = (Get-Command psql -ErrorAction SilentlyContinue)?.Source
if (-not $psql) {
    $fallback = 'C:\Program Files\PostgreSQL\15\bin\psql.exe'
    if (Test-Path $fallback) { $psql = $fallback }
    else { throw "psql not found on PATH and no fallback at $fallback. Install the Postgres client or add psql to PATH." }
}

# --- paths -----------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProj  = Join-Path $repoRoot 'src/MedicalAssistance.Ingestion.Api'
$connString = "Host=$DbHost;Port=$Port;Database=$Database;Username=$Username;Password=$Password"

$env:PGPASSWORD = $Password

function Invoke-Psql([string]$Db, [string]$Sql) {
    & $psql -h $DbHost -p $Port -U $Username -d $Db -v ON_ERROR_STOP=1 -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed (exit $LASTEXITCODE) running: $Sql" }
}

Write-Host "==> Dropping and recreating '$Database' on $DbHost`:$Port" -ForegroundColor Cyan
Invoke-Psql -Db 'postgres' -Sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();"
Invoke-Psql -Db 'postgres' -Sql "DROP DATABASE IF EXISTS $Database;"
Invoke-Psql -Db 'postgres' -Sql "CREATE DATABASE $Database;"

Write-Host "==> Applying migrations" -ForegroundColor Cyan
dotnet ef database update --project $apiProj --connection $connString
if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed (exit $LASTEXITCODE)." }

Write-Host "==> Done. '$Database' is fully migrated and ready for ingestion." -ForegroundColor Green
