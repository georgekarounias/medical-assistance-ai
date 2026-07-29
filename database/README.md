# Database & migration workflow

The service stores ingestion state **and** chunk vectors in a single Postgres
database with the `pgvector` extension (see
[ADR-0001](../docs/adr/0001-postgres-pgvector-for-status-and-vectors.md)). The
schema is owned by EF Core migrations — never by `EnsureCreated` or
schema-from-model bootstrapping.

This document is both the **quickstart** (bring the local DB up) and the
**reference** (how migrations work, the migration list, the vector index). All
paths are relative to the repo root (`ai-med/medical-assistance-ai`); run the
commands from a normal PowerShell terminal.

## Prerequisites

- **Docker Desktop** running.
- **.NET 10 SDK** with EF Core tools (`dotnet tool install --global dotnet-ef`).
- A **psql** client on PATH (the DB script falls back to
  `C:\Program Files\PostgreSQL\15\bin\psql.exe` if it can't find one).

## 1. Start the database container

Local development runs against a pgvector Postgres container:

| | |
|---|---|
| Image | `pgvector/pgvector:pg17` |
| Container | `ai-med-postgres` |
| Host / port | `localhost:5433` |
| Database | `ai_med` |
| Credentials | `postgres` / `postgres` |

The connection string lives in
`src/MedicalAssistance.Ingestion.Api/appsettings.Development.json` under
`ConnectionStrings:Postgres`.

Start it:

```powershell
docker start ai-med-postgres
```

If the container does not exist yet, create it once:

```powershell
docker run -d --name ai-med-postgres `
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=ai_med `
  -p 5433:5432 pgvector/pgvector:pg17
```

The integration test suite does **not** use this container — it spins up its own
throwaway `pgvector/pgvector:pg17` via Testcontainers (Docker Desktop required).

## 2. Create / reset the schema (run all migrations)

Use `createDB.ps1` to get a clean, fully-migrated database from scratch (e.g.
after pulling new migrations, or when the local DB has drifted). It drops and
recreates `ai_med`, then applies **every EF Core migration** in order via
`dotnet ef database update`:

```powershell
./database/createDB.ps1
```

> **Why drop first?** Several migrations are data-only seeds
> (`SeedAgentInstructions`, `SeedImagingChunker`, `SeedLabReportSummarizer`) that
> `INSERT` fixed rows. Re-running `dotnet ef database update` against a database
> that already holds those rows fails on a duplicate-key violation. Dropping first
> guarantees a clean apply. **On a production database with real data you would
> NOT drop** — you would only apply the pending migrations
> (`dotnet ef database update`).

## How migrations are applied

There are two entry points, and both apply the *same* migrations in the *same*
order (EF orders by the migration's timestamp prefix):

1. **At application startup.** `Program.cs` calls `db.Database.MigrateAsync()`
   under a Postgres advisory lock (`PostgresAdvisoryLock.SchemaMigrationKey`) so
   that in a rolling deploy exactly one instance migrates while the others wait.
   Immediately after, it calls `connection.ReloadTypesAsync()` — the `vector`
   extension may have just been created by the `InitialSchema` migration, and the
   Npgsql type catalog for this pooled connection was loaded before that, so the
   reload is what makes the `vector` type usable in the same process.

2. **Manually, via `createDB.ps1`.** The drop-and-recreate flow described in
   step 2 above.

## Adding a new migration

From the repo root:

```bash
dotnet ef migrations add <Name> --project src/MedicalAssistance.Ingestion.Api
```

Guidelines:

- The `vector(3072)` embedding column is fixed by the schema (see
  `IngestionDbContext.EmbeddingDimensions`). Changing the embedding dimension is a
  re-embedding migration, not a config toggle.
- Data-only seed migrations (no schema change) leave the model snapshot untouched —
  that is expected; see the comment in `SeedAgentInstructions`.
- Agent-instruction prompts are seeded **by migration**, not by application startup
  (ADR-0008), so a fresh database comes up ready to run.

## Migrations (in apply order)

| Order | Migration | What it does |
|---|---|---|
| 1 | `InitialSchema` | `vector` extension; `ingestions`, `chunks`, `agent_instructions` |
| 2 | `DocumentIdCarriesDoctorAndPatient` | data reshaping (no schema change) |
| 3 | `UnIngestDocumentIdAndTombstone` | un-ingest support + tombstone |
| 4 | `ErasureLog` | GDPR erasure audit table |
| 5 | `SeedAgentInstructions` | seed chunker/mapper prompts |
| 6 | `AnalyteResultsAndTier2` | `analyte_results` table + Tier-2 fields |
| 7 | `SeedImagingChunker` | seed the imaging-report chunker prompt |
| 8 | `ChunkEmbeddingModel` | record the embedding model per chunk |
| 9 | `ChunkPatientDoctorAndVectorIndexes` | `chunks` B-tree on `(patient_id, doctor_id)` + HNSW ANN index |
| 10 | `IngestionDocumentSummary` | `summary` column on `ingestions` (per-document summary) |
| 11 | `PatientRollingSummary` | `patient_summaries` table + seed the `PatientSummarizer` agent |
| 12 | `SeedLabReportSummarizer` | seed the `LabReportSummarizer` agent prompt (lab reports are rendered by code, so this agent writes their per-document summary) |
| 13 | `IngestionQualityReport` | `ingestion_quality_reports` table — persisted per-ingestion chunking quality metrics (T35) |

### The `chunks` vector index

Similarity search is patient-scoped (the security boundary), optionally narrowed to
one doctor, so `chunks` carries a B-tree on `(patient_id, doctor_id)` — one index that
serves both filters and keeps patient erasure off a table scan.

The ANN index is HNSW built over a **`halfvec(3072)` cast**, not the raw
`vector(3072)` column: pgvector's HNSW/IVFFlat indexes cap at 2000 dimensions for
full-precision vectors, and the embedding is 3072-dim. For the planned retrieval
service to actually use this index, its query must order by the same cast and cosine
distance:

```sql
SELECT ... FROM chunks
WHERE patient_id = @patientId            -- and optionally: AND doctor_id = @doctorId
ORDER BY embedding::halfvec(3072) <=> @query::halfvec(3072)
LIMIT @k;
```

The cast index lives in raw SQL inside migration 9 (EF cannot express a cast inside an
index expression), so it is not in the EF model snapshot — do not re-scaffold it.

## Verifying a fresh database

```powershell
# tables (expect: agent_instructions, analyte_results, chunks, erasure_log,
#                 ingestion_quality_reports, ingestions, patient_summaries)
docker exec -e PGPASSWORD=postgres ai-med-postgres psql -U postgres -d ai_med -c '\dt'

# every migration recorded (expect 13)
docker exec -e PGPASSWORD=postgres ai-med-postgres psql -U postgres -d ai_med `
  -c 'SELECT count(*) FROM "__EFMigrationsHistory";'

# pgvector present
docker exec -e PGPASSWORD=postgres ai-med-postgres psql -U postgres -d ai_med `
  -c "SELECT extname, extversion FROM pg_extension WHERE extname='vector';"
```

## (Optional) A web UI for the vector store

pgvector is stored in Postgres, so any Postgres admin UI works. Start **pgAdmin**
in its own container:

```powershell
docker run -d --name ai-med-pgadmin `
  -e PGADMIN_DEFAULT_EMAIL=admin@admin.com `
  -e PGADMIN_DEFAULT_PASSWORD=admin `
  -p 5050:80 dpage/pgadmin4
```

Then open <http://localhost:5050> (login `admin@admin.com` / `admin`) and register
a server pointing at the database:

| Field | Value |
|---|---|
| Host | `host.docker.internal` |
| Port | `5433` |
| Maintenance DB | `ai_med` |
| Username | `postgres` |
| Password | `postgres` |

> pgAdmin runs in its own container, so it reaches the Postgres container via
> `host.docker.internal` (not `localhost`).

**Lighter alternative — Adminer** (single-page, no login setup):

```powershell
docker run -d --name ai-med-adminer -p 8080:8080 adminer
```

Open <http://localhost:8080>, then connect with System `PostgreSQL`, Server
`host.docker.internal:5433`, User `postgres`, Password `postgres`, Database `ai_med`.

To stop the UI when done:

```powershell
docker stop ai-med-pgadmin    # or: ai-med-adminer
```

Connection string
-----------------
postgresql://postgres:postgres@host.docker.internal:5433/ai_med