---
title: Clinical Document Ingestion Service
labels: [ready-for-agent]
date: 2026-07-12
sources: [../ingestion-pipeline-design.md, ../../CONTEXT.md, ../adr/]
---

# PRD: Clinical Document Ingestion Service

## Problem Statement

A doctor records sessions with patients and receives dialog-like transcripts, and also accumulates other clinical material about each patient — personal notes, lab result PDFs, imaging reports. Today that knowledge is trapped inside individual documents: to recall what a patient said about their symptoms three sessions ago, or how a lab value has changed over the year, the doctor must re-read source material document by document. There is no way to simply *ask* about a patient and get a trustworthy, source-grounded answer.

Before any question can be answered, the material has to be reliably captured: uploaded documents must become searchable quickly, the doctor must see that ingestion succeeded (or honestly failed), and mistakes — like attaching a document to the wrong patient — must be correctable by removing the document and everything derived from it.

## Solution

A new .NET 10 web service that ingests clinical Documents (session Transcripts, Doctor Notes, Lab Reports, Imaging Reports) into a patient-scoped knowledge store (Postgres + pgvector), preparing the ground for a future RAG chat. The existing backend submits documents on the doctor's behalf; the service processes them through per-type Ingestion Strategies — LLM semantic chunking for prose, table-safe extraction for lab PDFs — and reports progress in real time over SignalR so the doctor's app can show live status. Every stored word of patient text is verbatim; no generative model ever produces a number or sentence that gets stored as patient data. Ingestions are atomic (fully visible or not at all), corrections supersede cleanly, duplicates are absorbed silently, and the doctor can browse and un-ingest any document. This PRD covers ingestion and un-ingestion only; the chat/retrieval side is future work.

## User Stories

1. As a doctor, I want my session transcript ingested shortly after upload, so that the session's content is searchable while it is still fresh.
2. As a doctor, I want to see live status of an ingestion (queued, processing with stage, completed, failed), so that I know when the material is available without polling or guessing.
3. As a doctor, I want a clear failure notice with a reason when an ingestion fails, so that I never believe data is searchable when it is not.
4. As a doctor, I want to retry a failed ingestion without re-uploading, so that transient problems don't cost me the original material.
5. As a doctor, I want a re-transcribed or corrected transcript to replace the old one completely, so that questions about the session never surface stale, contradicting text.
6. As a doctor, I want a session that produced several recordings (pause, dead battery, resumed session) to be ingested as sibling transcripts of one session, so that no part of the encounter is missing.
7. As a doctor, I want an accidental double-upload of the same content absorbed as a harmless duplicate, so that I don't create noise in the patient's record.
8. As a doctor, I want my typed notes about a patient ingested, so that my own observations are part of what I can ask about.
9. As a doctor, I want an edited note to supersede its earlier version, so that only my latest wording is retrievable.
10. As a doctor, I want a note optionally linked to a session, so that context about when I wrote it is preserved.
11. As a doctor, I want lab result PDFs ingested as readable text, so that I can later ask "what were her last blood results?" and get the report's content.
12. As a doctor, I want each measured lab value captured exactly as printed (value, unit, reference range, flag), so that a future chat can answer trend questions like "how has his HbA1c moved this year?" with numbers I can trust.
13. As a doctor, I want lab values that could not be verified against the source PDF to be visibly absent rather than silently partial, so that a trend never quietly omits a data point.
14. As a doctor, I want imaging reports ingested as text with a link to the actual image, so that I can read the findings in chat and jump to the image in my existing viewer.
15. As a doctor, I want everything to work in both Greek and English, in any mix, so that my real-world documents are all first-class.
16. As a doctor, I want to browse the list of a patient's ingested documents (type, date, status), so that I can see what the system knows about the patient.
17. As a doctor, I want to un-ingest a document I uploaded by mistake — the wrong-patient transcript being the classic case — so that the patient's record contains only true material, with every derived chunk and value gone.
18. As a doctor, I want simultaneous uploads for different patients and sessions to process concurrently, so that a busy day doesn't queue my afternoon behind my morning.
19. As a doctor, I want my patients' data isolated by patient and by doctor, so that retrieval can never leak across patients or practices.
20. As a patient, I want my right to erasure honored — all my data removable in one administrative act — so that my privacy is enforceable, not aspirational.
21. As a compliance officer, I want un-ingestion to leave an audit tombstone (who removed what, when) while erasure removes even those, so that deletion is accountable and erasure is complete.
22. As a compliance officer, I want all AI processing (LLM, embeddings, document parsing) confined to EU-region services under our data-processing agreements, so that GDPR Article 9 obligations are met.
## Implementation Decisions

Vocabulary throughout is the project glossary (CONTEXT.md); the load-bearing choices are recorded as ADRs 0001–0006 and summarized here.

### Engineering constraints

These are not features — they are the product owner's restrictions on *how* the system must be built, so that a clinic-facing medical system stays clean, trustworthy, and straightforward to work on:

- **The codebase must be readable by a developer new to it.** The pipeline reads as plain sequential stages selected by a deterministic router — no workflow engine, no framework ceremony hiding four method calls.
- **Expansion must be routine, not surgery.** A new document type is one new Ingestion Strategy (its stages, agents, identity rule); intake, queueing, status, and storage are never touched.
- **AI providers are configuration, not architecture.** Everything reaches models through provider abstractions; swapping providers or models is a config change.
- **No half-states, ever.** Every ingestion is atomic — fully committed or rerun from scratch — so no developer ever reasons about partially ingested data.
- **Nothing is lost silently.** Crash recovery is automatic (non-terminal ingestions re-enqueued at startup), bounded by attempt caps so a pathological document cannot consume the service.
- **The system explains itself.** Stage-correlated structured logs, traces around every AI call, and outcome/duration metrics are part of the build, not an afterthought.

### Decisions

- **Service shape.** One .NET 10 web service. The existing backend is the sole caller, authenticated by a shared API secret sent as a request header on REST calls and on the SignalR hub handshake — no user tokens, no JWT. User context (doctorId, patientId, …) is passed explicitly in each request payload; authorization (which doctor may do what) is the backend's responsibility, and this service fully trusts its one caller. Erasure requires a separate admin secret. Secrets live in the estate's secret store with dual active keys for zero-downtime rotation. The doctor's phone app never talks to this service.
- **Intake.** `POST /ingestions` with `documentType` as discriminator (SessionTranscript, DoctorNote, LabReport, ImagingReport) and per-type payload validation. Transcripts arrive as inline free text (dialog-like, one utterance per line by convention, but the service does not require structure); notes as inline text; lab and imaging reports as size-capped base64 PDFs. Returns 202 with an ingestion id; 409 when the same document identity is already queued or processing.
- **Identity and dedup per type.** Transcript = (sessionId, sequenceNumber): a new sequence number is a Continuation, a reused one is a Correction. Note = noteId (edit = Correction). Lab/Imaging = backend-assigned document id plus content-hash dedup. Platform rule: an identical re-POST after success is a no-op (`duplicate: true`); after failure it is a retry. A Correction transactionally supersedes the prior document's chunks and derived rows.
- **Orchestration.** The Orchestrator is a deterministic registry lookup from Document Type to Ingestion Strategy — never content inference (ADR-0004). Strategies are plain sequential stages; AI lives inside stages as Microsoft Agent Framework ChatClientAgents behind `IChatClient`/`IEmbeddingGenerator`. Day-one providers: Azure OpenAI EU region (gpt-4.1-mini for chunking/enrichment, text-embedding-3-large for multilingual embeddings) — providers are configuration, not architecture.
- **Agent instructions are data, not code.** Every agent's instructions are stored in the database (seeded from reviewed code defaults) with a version, loaded once at application start into a singleton provider that strategies build their agents from. Prompts are tunable per environment via a restart, without redeploying; code-side guardrails keep the output contract enforced regardless of prompt wording (ADR-0008). Every ingestion records the instruction version and models that processed it — provenance for quality debugging and selective re-ingestion.
- **Processing model.** Durable-first: every POST persists an Ingestion record before enqueueing on an in-process channel consumed by a configurable number of background workers (default ~4; the true ceiling is provider rate limits). Startup recovery re-enqueues non-terminal ingestions with an attempt cap (3). No cross-document ordering constraints; only same-identity concurrency is blocked. Embedding calls are batched per document.
- **Transcript/Note strategy (prose pipeline).** Semantic chunking is boundaries-only (ADR-0002): code numbers the text's non-empty Lines; the LLM returns line-index ranges, a Context Blurb per Chunk, and a Transcript Summary as structured output; code assembles chunk text verbatim from the pointed-at Lines, validates boundaries (contiguous, complete, non-overlapping), and enforces size guardrails (~200–600 token target, 800 hard max). One corrective retry, then Failed — an explicit decision preferring honest failure over degraded chunking. Embedding input is blurb + verbatim text; displayed text is always verbatim.
- **LabReport strategy.** Azure Document Intelligence (layout model) extracts text and table cell grids from digitally generated PDFs (ADR-0005). Tier 1: code deterministically renders one Rendition per Panel (one chunk per panel, never per analyte) — required for the ingestion to complete. Tier 2: an LLM classifies columns and canonical analyte names; code copies every value from the cells the LLM pointed at and verifies each verbatim against the source grid (ADR-0006). Analyte Result rows are all-or-nothing per document; on any verification failure zero rows are stored and the record carries `analytesExtracted: false`.
- **ImagingReport strategy.** Document Intelligence text extraction feeding the prose pipeline; every chunk carries the image link as its source reference. Pixel data is never ingested.
- **Storage.** Postgres + pgvector holds ingestion state, chunks/vectors, and Analyte Result rows (ADR-0001). Every ingestion commits in one final transaction — delete superseded artifacts, insert new, mark Completed — so nothing is ever partially visible (ADR-0003); crash or retry means rerun from scratch. Raw payloads are stored (retry, rerun, audit, provenance), making the service a PHI system of record.
- **Chunk metadata spine** (the schema designed once because reshaping a populated vector table hurts): patientId (universal retrieval filter and security boundary), doctorId, documentType, documentId (cascade-delete target — never a transcript-specific column name), ingestionId, documentDate (clinical date, not upload time), language, chunkKind, nullable sourceRef (line range / PDF page / image link), nullable sessionId, and the embedding model that produced the vector (the write/read contract — a model change becomes a managed re-embedding migration, never silent search corruption).
- **Status.** Persisted states: Queued → Processing → Completed | Failed | Deleted. Stage-level detail rides on SignalR events only. Failed records an error category and message. Transient faults retry inside stages with backoff and never surface as states.
- **Status delivery.** SignalR hub on this service; the backend connects as the single trusted client, receives doctorId-stamped events, and fans out to devices itself. REST (`GET /ingestions?...&active=true`) is the reconnect source of truth; events are a hint to look.
- **Lifecycle.** `GET /patients/{patientId}/documents` powers the doctor's ingested-documents view. `DELETE /documents/{documentId}` is Un-ingest: chunks, analyte rows, and payload removed; the Ingestion record survives as a Deleted audit tombstone. `DELETE /patients/{patientId}/data` is Erasure: GDPR removal of everything including tombstones, itself irreversibly logged, and guarded by the separate admin secret.

## Testing Decisions

- **One seam: the service's public boundary.** Tests host the whole app in-process (ASP.NET Core WebApplicationFactory), POST real documents, and assert only externally observable behavior: status transitions via GET, events received by a test SignalR client, chunks and analyte rows present in Postgres, supersede and un-ingest cascades, 409/duplicate semantics. No test reaches into pipeline internals.
- **Fakes only at the three boundaries the architecture already defines** — no seams invented for testing: `IChatClient` (scripted structured outputs for the chunking agent and lab mapper, including malformed-output cases to exercise validation and the corrective retry), `IEmbeddingGenerator` (deterministic vectors), and the document-extraction interface over Document Intelligence (canned cell grids and text from golden PDFs).
- **Postgres stays real** (Testcontainers): transactional supersede, atomic visibility, and pgvector behavior are the product; faking the store would test nothing.
- **What makes a good test here:** it describes a behavior a doctor or the backend depends on (e.g., "a Correction leaves no stale chunks", "an unverifiable analyte row stores zero rows and flags the record", "a crash before commit leaves nothing visible and the ingestion reruns"), and it would survive a rewrite of the pipeline internals.
- **Golden inputs:** a golden-transcript set (Greek, English, mixed) and a golden-PDF set per lab provider — the per-provider PDF set doubles as the Tier-2 quality gate called out in the design record.
- **Prior art:** none — this is the first code in the repo; these tests establish the pattern.

## Out of Scope

- The RAG chat / retrieval side (query answering, prompt design, retrieval ranking) — this PRD ends at a correctly populated, correctly scoped store.
- Changes to the phone app and the existing backend (upload UX, status relay UI, the backend's hub subscription) beyond the contracts defined here.
- Structured clinical-fact extraction from dialog (medications, conditions, symptoms, follow-ups) — deferred with its guardrail debate; unlike lab analytes it is generative extraction.
- OCR for scanned or handwritten documents — only digitally generated PDFs are supported (ADR-0005 records the re-entry point).
- LOINC or similar formal coding of analytes (additive on canonical names later).
- Session-level artifacts such as rolling session summaries (would force per-session ordering, deliberately rejected).
- External message broker, stage checkpointing, blob storage for payloads, and a classifier agent for untyped documents — all recorded as deliberate deferrals with re-entry points in the design record.
- Compliance sign-off itself (EU-region processing approval, retention policy for tombstones) — tracked as open items, not engineering work.

## Further Notes

- The two safety invariants that define the system's character and must survive every future change: **no generative model ever produces a number or a word of patient text that gets stored** (the LLM points, code copies — ADRs 0002/0006), and **nothing is ever partially visible** (ADR-0003 plus tiered honesty for labs).
- Start collecting real PDFs from each lab provider now; "many lab formats" makes Tier-2 extraction quality an empirical, per-provider question, and the golden-PDF set is the gate.
- Open items before/alongside implementation: compliance sign-off (Azure OpenAI + Document Intelligence, EU regions; encryption-at-rest and backup rules for the new database), and provisioning of the two API secrets (standard + admin) in the estate's secret store with an agreed rotation procedure.
- Full decision narrative: docs/ingestion-pipeline-design.md; glossary: CONTEXT.md; ADRs 0001–0006.
