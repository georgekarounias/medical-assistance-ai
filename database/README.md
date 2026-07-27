# Database & migration workflow

The service stores ingestion state **and** chunk vectors in a single Postgres
database with the `pgvector` extension (see
[ADR-0001](../docs/adr/0001-postgres-pgvector-for-status-and-vectors.md)). The
schema is owned by EF Core migrations — never by `EnsureCreated` or
schema-from-model bootstrapping.

## The dev database

Local development runs against a pgvector Postgres container:

| | |
|---|---|
| Image | `pgvector/pgvector:pg17` |
| Container | `ai-med-postgres` |
| Host / port | `localhost:5433` |
| Database | `ai_med` |
| Credentials | `postgres` / `postgres` |

Connection string lives in
`src/MedicalAssistance.Ingestion.Api/appsettings.Development.json` under
`ConnectionStrings:Postgres`.

Start it:

```bash
docker start ai-med-postgres
```

If the container does not exist yet, create it once:

```bash
docker run -d --name ai-med-postgres \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=ai_med \
  -p 5433:5432 pgvector/pgvector:pg17
```

The integration test suite does **not** use this container — it spins up its own
throwaway `pgvector/pgvector:pg17` via Testcontainers (Docker Desktop required).

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

2. **Manually, via `createDB.ps1`.** Use this to get a clean, fully-migrated
   database from scratch (e.g. after pulling new migrations, or when the local DB
   has drifted). It drops and recreates `ai_med`, then runs
   `dotnet ef database update`.

```powershell
./database/createDB.ps1
```

> **Why drop first?** Some migrations are data-only seeds
> (`SeedAgentInstructions`, `SeedImagingChunker`) that `INSERT` fixed rows. Re-running
> `dotnet ef database update` against a database that already holds those rows fails
> on a duplicate-key violation. Dropping first guarantees a clean apply. For a
> production database with real data you would **not** drop — you would only apply
> the pending migrations (`dotnet ef database update`).

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

```bash
# tables (expect: agent_instructions, analyte_results, chunks, erasure_log, ingestions)
docker exec -e PGPASSWORD=postgres ai-med-postgres psql -U postgres -d ai_med -c "\dt"

# every migration recorded (expect 8)
docker exec -e PGPASSWORD=postgres ai-med-postgres psql -U postgres -d ai_med \
  -c 'SELECT count(*) FROM "__EFMigrationsHistory";'

# pgvector present
docker exec -e PGPASSWORD=postgres ai-med-postgres psql -U postgres -d ai_med \
  -c "SELECT extname, extversion FROM pg_extension WHERE extname='vector';"
```
