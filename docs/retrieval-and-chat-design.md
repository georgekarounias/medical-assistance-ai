# Retrieval & Grounded Chat Pipeline — Design Record

Outcome of the design interview, 2026-07-30, adapting the Consultant-AI retrieval blueprint (`docs/helping_documents_out_of_repo/helping_project/docs/medical-assistant-retrieval/`) to this service. Terminology: [CONTEXT.md](../CONTEXT.md). Load-bearing decisions: [ADR-0010](adr/0010-retrieval-and-chat-here-conversations-in-backend.md), [ADR-0011](adr/0011-retrieval-searches-authoritative-pgvector-no-hydration.md), [ADR-0012](adr/0012-grounded-chat-verifies-before-release-and-refuses-without-evidence.md).

## What is being built

Two new capabilities on the existing ingestion service, over the Chunks it already stores:

1. **Retrieval** — given a patient scope and a question, return the most relevant verbatim Chunks as ranked Evidence Items with provenance.
2. **Grounded answering** — given a question, optional Conversation Context, and retrieved evidence, return a cited clinical Grounded Answer, or refuse with Insufficient Evidence.

Scope of this design: **retrieval + stateless grounded answering only.** Conversations, message history, idempotency, conversation memory, and titles are the **backend's** responsibility and are out of scope here (ADR-0010). The backend does not exist yet; these endpoints are built ahead of it against the trusted-single-caller contract.

## How this differs from the Consultant-AI blueprint

The blueprint was written against Consultant-AI's architecture. Four differences change the design and are the reason it is *adapted*, not copied:

| Blueprint (Consultant-AI) | This service | Effect |
| --- | --- | --- |
| Two stores: vector engine + separate SQL system of record | **One store**: pgvector *is* the authoritative record (ADR-0001) | Blueprint step 5 "hydrate & validate" and overfetch **removed** (ADR-0011) |
| Per-user cookie auth; authorize resolves project scope | Shared API secret; `patientId`/`doctorId` trusted from payload (ADR-0007) | "Authorize" becomes "scope every query by `patient_id`" — the security filter |
| Project-scoped corpus of uploaded documents | **One patient's own clinical record** | No source-authority hierarchy; "freshness" = clinical `DocumentDate` |
| Rolling summary = conversation memory; personas | Conversation memory is the backend's; one fixed clinical voice | No persona subsystem; memory arrives as input, is never stored (ADR-0010) |

Note two pre-existing, unrelated summaries that are **not** conversation memory: `IngestionRecord.Summary` (per-document) and `PatientSummary` (rolling per-patient, from ingestion). The latter is a candidate stable-context input to answer generation; neither is the "conversation rolling summary" the blueprint means.

## System boundary

- The **existing backend** is the only caller, authenticated by the same **shared API secret** as ingestion (`X-Api-Key`; ADR-0007). No user tokens: `doctorId`/`patientId` travel in the payload and are trusted; the who-asked-what user audit is the backend's.
- This service stays **stateless about chat** — it persists no conversations, messages, or memory. The grounded-answer endpoint is a pure function of its inputs (ADR-0010).
- The **doctor's app never talks to this service.** Any live token streaming to the app is relayed by the backend's own channel; this service returns complete answers (ADR-0012).

## Retrieval pipeline

Five ordered steps over a shared request-scoped `RetrievalContext`, structured as an **ordered-step registry**: each step is an `IRetrievalStep` with an `Order`, resolved from DI, sorted, and run in sequence against the mutable context. New steps (the deferred hybrid-search or structured-analyte steps) slot in by registration. (The blueprint's sixth step, hydration, is removed — ADR-0011.)

| Order | Stage | Responsibility |
| ---: | --- | --- |
| 10 | **Scope** | Establish the mandatory `patient_id` filter (the security boundary) and optional `doctor_id` / `document_type` / clinical-date / `session_id` filters. No permission call — the backend is trusted; scope is a query constraint. |
| 20 | **Refine** *(optional)* | LLM rewrites the question into a cleaner search query using Conversation Context. Config-gated; fails open to the raw question. Affects only the query vector. |
| 30 | **Embed** | Embed the effective query with the **same model/dimensions as ingestion** (`text-embedding-3-large`, 3072). |
| 40 | **Search** | ANN scan over the authoritative `chunks` rows within scope: `ORDER BY embedding::halfvec(3072) <=> query::halfvec(3072)` (matches the HNSW cast index), `LIMIT TopK`. Filters in the same `WHERE`; no overfetch. |
| 50 | **Package** | Drop hits below the Confidence Threshold; return the survivors as Evidence Items (score + provenance + verbatim text), highest score first. |

**Failure behavior:** blank queries rejected; refinement fails open; embedding/search failures fail the request after transient retries; no hits (or none above threshold) → an empty evidence set, which the answer path turns into Insufficient Evidence.

## Grounded-answer path

The answer path composes retrieval with generation and the safety rules (ADR-0012):

```
Build prompt inputs  (question + Conversation Context + optional patient stable context)
   -> Retrieve        (the pipeline above)
   -> if no Evidence Items above threshold: return Insufficient Evidence (200, 0 citations)
   -> Generate        (MAF agent, DB-seeded prompt (ADR-0008); answer only from [E#] evidence,
                       in the question's language, one fixed clinical voice)
   -> Verify grounding (every [E#] cited was supplied this turn and still resolves)
   -> Return          complete Grounded Answer + verified Citations
```

- **Non-streaming in v1** — the full answer is generated and verified before it is returned (ADR-0012). Nothing ungrounded is ever emitted.
- **Agents are DB-seeded** like every other agent (ADR-0008): a `GroundedChat` answering prompt and (if refinement is enabled) a `QueryRefinement` prompt, added as seed migrations.
- **Conversation Context is input only** — used for pronoun resolution / refinement / phrasing, never stored, never an evidence source (ADR-0010). Any medical fact is re-retrieved and grounded on the current turn.

## Contract

### Public surface

Only the grounded-answer endpoint is HTTP-exposed in v1; retrieval is an internal `IRetrievalService` the answer path calls (directly tested, not versioned as a public API). It is promoted to an endpoint later if a second consumer needs raw evidence.

| Endpoint | Purpose |
| --- | --- |
| `POST /patients/{patientId}/chat/answer` | Grounded answering. Backend-mediated, secret-authenticated (`X-Api-Key`). Stateless. |

**Request** — `patientId` in the route (the hard boundary); body:

```json
{
  "doctorId": "…",                 // asking doctor — audit/telemetry, always present
  "question": "…",
  "recentTurns": [ { "role": "user|assistant", "text": "…" } ],   // optional, bounded
  "priorSummary": "…",             // optional, bounded — conversation memory from the backend
  "topK": 8,                        // optional; clamped 1..50, default 8
  "filters": {                      // all optional
    "doctorId": "…",               // NARROW to one doctor's documents (distinct from the asking doctorId)
    "documentType": "SessionTranscript|DoctorNote|LabReport|ImagingReport",
    "from": "2026-01-01T00:00:00Z", // clinical-date range (DocumentDate)
    "to":   "2026-07-01T00:00:00Z",
    "sessionId": "…",
    "language": "el|en"            // optional filter; retrieval is cross-language by default
  }
}
```

**Response** — `200` for both an answer and a refusal:

```json
{
  "answer": "…",                   // grounded prose, OR the deterministic refusal text
  "refused": false,                // true ⇒ Insufficient Evidence
  "retrievalUsed": true,
  "language": "el",                // language the answer was written in
  "citations": [
    { "label": "E1", "chunkId": "…", "documentId": "…", "documentType": "LabReport",
      "sessionId": null, "documentDate": "2026-05-02T00:00:00Z",
      "sourceRef": "{\"startLine\":0,\"endLine\":3}", "quote": "…bounded verbatim…", "score": 0.83 }
  ]
}
```

### Status codes

| Code | When |
| --- | --- |
| `200` | A grounded answer **or** an Insufficient-Evidence refusal (`refused: true`, empty `citations`) — a refusal is a normal outcome, not an error. Also the response for an unknown/empty patient (no chunks): a refusal, not a 404, so patient existence is not leaked and no patient registry is assumed. |
| `400` | Blank question, malformed filters, `topK` out of range after clamp guard. |
| `401` | Missing/invalid API secret. |
| `5xx` | Grounding/citation verification failed (no retry — fail fast, ADR-0012); or embedding/search/generation provider failure after transient retries. |

### Behavioral rules

- **Refusal text is deterministic and code-owned**, selected by the question's language (el/en) — no model call on the refusal path.
- **Verification failure fails the turn (5xx), no corrective retry** (ADR-0012). Nothing unverified is ever emitted; the caller decides whether to retry.
- **Doctor scope**: `patientId` is the only hard boundary; the asking `doctorId` is audit-only; `filters.doctorId` optionally narrows to one doctor's documents.
- **Conversation Context is bounded and never stored** — `recentTurns`/`priorSummary` are clamped defensively and used only for refinement/phrasing (ADR-0010).

## Evidence Item & Citation shape

Evidence Item, from the `chunks` row: `chunkId`, `documentId`, `documentType`, `chunkIndex`, `sessionId?`, `documentDate?`, `language?`, `chunkKind`, `sourceRef` (e.g. line range), `verbatimText`, `score`. Citation returned inline (above); when the backend owns conversations it persists these as durable citation records. Provider DTOs (pgvector/Npgsql) stay inside infrastructure — the response exposes evidence and provenance without leaking them.

## Medical adaptations — decided vs. deferred

**In v1 (ADR-0012):** Confidence Threshold · Insufficient-Evidence refusal · Citation verification · answer-in-question-language / cross-language retrieval · telemetry-only audit (ADR-0009, ids & counts, no patient text).

**Deferred / reframed:** structured Analyte-trend answers (a separate later tool with its own merge/ranking policy) · hybrid lexical+vector search for exact drug names / lab codes (recall improvement, calibrate need against T36) · source-authority hierarchy (N/A — single-patient corpus) · document-type weighting (a retrieval-quality knob, not a trust tier).

## Open items to calibrate against the golden set (T36)

1. Confidence Threshold value(s) — the floor that makes refusal fire when and only when it should.
2. `TopK` and how many Evidence Items to place in the generation prompt.
3. Whether pure vector recall is adequate for exact medical terminology, or hybrid search is needed.
4. Acceptance targets: retrieval recall/precision, refusal correctness, citation correctness.
