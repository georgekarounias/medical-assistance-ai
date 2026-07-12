# Ingestion Pipeline — Design Record

Outcome of the design interviews, 2026-07-12 (initial transcript pipeline + multi-document expansion). Terminology: [CONTEXT.md](../CONTEXT.md). Load-bearing decisions: [docs/adr/](adr/).

## What is being built

A .NET 10 web service that ingests clinical Documents into Postgres + pgvector so a future RAG chat can answer a doctor's questions about a patient. Four Document Types: **SessionTranscript**, **DoctorNote**, **LabReport**, **ImagingReport**. Scope of this design: **ingestion and un-ingestion only** — the chat/retrieval side is future work.

## System boundary

- The **existing backend** is the only caller, authenticated by a **shared API secret** sent as a request header (the SignalR hub handshake presents the same secret). There are no user tokens: **user context (doctorId, patientId, …) travels explicitly in each request payload**, and authorization (which doctor may do what) is the backend's responsibility — this service fully trusts its one caller. Erasure is guarded by a **separate admin secret**. Secrets live in the estate's secret store; validation supports two active keys so rotation is zero-downtime. The **doctor's phone app never talks to this service** — uploads, status, document lists, and un-ingest requests are all backend-mediated.
- Status flows: this service → SignalR hub → existing backend (the one trusted hub client) → backend's own channel to the app. Events are stamped with `doctorId`; the backend does the fan-out.
- This service **stores raw document payloads** (retry + rerun + audit + provenance need them) → it is a system of record for PHI and inherits the estate's encryption-at-rest/backup rules. Flagged to compliance owner.

## Document types

| Type | Arrives as | Identity | Strategy shape |
| --- | --- | --- | --- |
| SessionTranscript | Inline JSON speaker turns | (`sessionId`, `sequenceNumber`) | LLM semantic chunking (boundaries-only) + blurbs + summary → embed → store |
| DoctorNote | Inline text, optional `sessionId` link | `noteId` (backend-assigned; edit = Correction) | Same prose pipeline as transcripts (monologue mode) |
| LabReport | Digitally generated PDF (base64, size-capped), many lab layouts | Backend-assigned document id + content-hash dedup | Document Intelligence → Renditions (Tier 1) + Analyte Results (Tier 2) |
| ImagingReport | PDF of radiologist's findings + link to image | Backend-assigned document id + content-hash dedup | Document Intelligence text → prose pipeline; image link rides as chunk metadata. Pixels never ingested (ADR-0005) |

Scanned/handwritten documents (no text layer) are **out of scope** — no OCR path exists; re-entry point in ADR-0005.

## Contract

`POST /ingestions` — one endpoint, `documentType` as discriminator, per-type payload validation:

```json
{
  "documentType": "SessionTranscript",
  "doctorId": "…", "patientId": "…",
  "sessionId": "…", "sequenceNumber": 1,
  "sessionDate": "2026-07-10T14:30:00Z", "language": "el",
  "turns": [ { "speaker": "Doctor", "text": "…", "startTime": "00:00:04" } ]
}
```

`DoctorNote` carries `noteId` + `text` (+ optional `sessionId`); `LabReport`/`ImagingReport` carry base64 `pdfContent` + document id (+ `imageLink` for imaging).

- Returns `202` + `ingestionId`. `409` if the same document identity is already Queued/Processing.
- Identical content hash + prior success → no-op (`duplicate: true`). Same identity + different content → **Correction** (supersedes old chunks/rows transactionally). New `sequenceNumber` on an existing session → **Continuation**.

Full surface:

| Endpoint | Purpose |
| --- | --- |
| `POST /ingestions` | Submit a Document |
| `GET /ingestions/{id}` | Status of one ingestion |
| `GET /ingestions?doctorId=…&active=true` | Backfill/resync for the relay (REST is source of truth; SignalR events are a hint to look) |
| `POST /ingestions/{id}/retry` | Re-run a Failed ingestion (raw payload is stored) |
| `GET /patients/{patientId}/documents` | Ingested-documents view for the doctor's app |
| `DELETE /documents/{documentId}` | **Un-ingest**: removes chunks + analyte rows + payload; Ingestion record survives as a `Deleted` audit tombstone (who/what/when). Canonical case: wrong-patient upload |
| `DELETE /patients/{patientId}/data` | **Erasure** (GDPR, requires the separate admin secret): purges everything for a patient including tombstones and payloads; the erasure act itself is logged irreversibly |
| `/hubs/ingestion-status` | SignalR hub the backend subscribes to |

## Processing model

- POST → persist Ingestion row (`Queued`) → in-process `Channel` → N `BackgroundService` workers (config, default ~4; real ceiling is Azure OpenAI rate limits).
- Startup recovery re-enqueues non-terminal ingestions; attempt counter (max 3) prevents poison loops.
- No cross-document ordering; only same-identity concurrency is blocked (the 409).
- The **Orchestrator** is a deterministic registry lookup `documentType → IngestionStrategy` (ADR-0004). Strategies are plain sequential C# stages; AI lives inside stages as MAF `ChatClientAgent`s behind `IChatClient`/`IEmbeddingGenerator` (day one: Azure OpenAI EU, `gpt-4.1-mini` + `text-embedding-3-large`; provider is config, not architecture).
- Every ingestion is **atomic** (ADR-0003): nothing visible until the final single Postgres transaction (delete superseded chunks/rows + insert + `Completed`); crash/retry = rerun from scratch.

## Strategies

**SessionTranscript** — validate/persist → chunk+enrich (one structured-output agent call: turn-boundary ranges + Context Blurb per chunk + Transcript Summary; code assembles verbatim text, validates boundaries, enforces ~200–600-token targets / 800 hard max; one corrective retry then `Failed` — no degraded fallback, ADR-0002) → batched embed (blurb + verbatim text) → atomic store (summary embedded as its own chunk).

**DoctorNote** — same prose pipeline; input is monologue text instead of turns; chunk boundaries are paragraph/topic ranges; supersede on `noteId` re-POST.

**LabReport** — validate/persist → Document Intelligence layout extraction (text + table cell grids) → **Tier 1:** deterministic Rendition per Panel (code renders "Hemoglobin: 13.2 g/dL (ref 13.5–17.5, LOW)…" — one chunk per Panel, never per analyte) → **Tier 2:** analyte mapping (LLM classifies columns + canonical names; code copies values from the cells the LLM pointed at; verbatim verification per row — ADR-0006) → embed Renditions → atomic store of chunks + analyte rows.
Failure policy ("tiered honesty"): Tier 1 must succeed or the ingestion is `Failed`; Tier 2 rows are all-or-nothing — any unverifiable row stores zero rows and sets `analytesExtracted: false` (queryable, re-processable). Trend queries never see a partial Panel.

**ImagingReport** — Document Intelligence text extraction → prose pipeline (chunks + blurbs + summary) → every chunk carries the `imageLink` in `sourceRef`.

## Chunk metadata spine

The common core every chunk carries, designed once because reshaping a populated vector table is the migration that hurts. Type-specific data hangs off `sourceRef`; the parent column is `documentId`, never `transcriptId`.

| Field | Why |
| --- | --- |
| `patientId` | The universal retrieval filter and security boundary |
| `doctorId` | Access scoping, audit |
| `documentType` | Filter/weight by kind ("check her labs") |
| `documentId` | Cascade-delete target; provenance to source |
| `ingestionId` | Which run produced this chunk |
| `documentDate` | Clinical date (session/collection date), not upload time — powers "last X-ray" |
| `language` | `el`/`en` |
| `chunkKind` | `dialog` / `summary` / `note` / `labPanel` / `imagingReport` |
| `sourceRef` (nullable) | Turn range / PDF page / image link |
| `sessionId` (nullable) | Transcripts and session-linked notes only |

**Analyte Result rows** (relational, beside the vector table): `canonicalName`, `verbatimName`, value, unit, reference range, flag, collection date, `documentId`, provenance (page/table/row). Exist for trend queries vector search cannot answer.

## Status model

Persisted: `Queued → Processing → Completed | Failed | Deleted` (tombstone). Stage detail rides on SignalR events: `{ ingestionId, doctorId, state, stage, message }` (stages per strategy, e.g. `Chunking | Embedding | Storing`, `Extracting | Mapping | Embedding | Storing`). `Failed` records error category + message. Transient faults (429s/timeouts) retry inside stages with backoff (Polly) and never surface as states.

## Deferred — deliberately out of scope, with re-entry points

| Deferred | Re-entry point |
| --- | --- |
| Structured clinical-fact extraction from dialog (medications, conditions, symptoms, follow-ups) | New stage + agent in the transcript strategy; schema + mandatory provenance sketched in interview Q7. Unlike lab analytes, this is generative extraction — the full guardrail debate applies |
| OCR for scanned/handwritten documents | Document Intelligence supports it; re-validate quality per ADR-0005 before opening scope |
| LOINC (or similar) coding of analytes | Additive on `canonicalName` (ADR-0006) |
| Session-level artifacts (rolling session summary) | Would force per-session ordering — rejected; revisit only with the feature |
| Message broker | Replace the Channel; Ingestion table already durable |
| Stage checkpointing | Rejected as premature (ADR-0003); reconsider only with volume evidence |
| Classifier agent for untyped documents | Front stage with confidence threshold + human fallback (ADR-0004) |
| Blob storage for payloads | Only if PDFs outgrow Postgres `bytea` comfort (a few MB is fine) |

## Open items (not design, but must happen)

- Compliance sign-off: Azure OpenAI **and Document Intelligence** EU-region processing of patient data; encryption-at-rest/backup rules for the new Postgres DB; retention policy for `Deleted` tombstones.
- Provision the two API secrets (standard + admin) in the estate's secret store and agree the rotation procedure with the backend team (dual active keys, ADR-0007).
- Per-lab extraction quality: spot-check Document Intelligence + analyte mapping against real PDFs from each lab provider before trusting Tier 2 (a golden-PDF test set per provider).
- Observability when building: structured logs per stage with `ingestionId` correlation, OpenTelemetry traces around LLM/embedding/Document Intelligence calls, counters by outcome + duration + `analytesExtracted`.
- Testing: golden-transcript and golden-PDF integration tests through the real pipeline with fake `IChatClient`; contract tests for boundary validation and verbatim verification.
