---
title: Retrieval & Grounded Chat
labels: [ready-for-agent]
date: 2026-07-30
sources: [../retrieval-and-chat-design.md, ../../CONTEXT.md, ../adr/0010-retrieval-and-chat-here-conversations-in-backend.md, ../adr/0011-retrieval-searches-authoritative-pgvector-no-hydration.md, ../adr/0012-grounded-chat-verifies-before-release-and-refuses-without-evidence.md]
---

# PRD: Retrieval & Grounded Chat

## Problem Statement

The Clinical Document Ingestion Service has made a patient's material searchable — Transcripts, Doctor Notes, Lab Reports, and Imaging Reports are all chunked, embedded, and stored patient-scoped. But a doctor still cannot *ask* about a patient. To recall what a patient said about their symptoms three sessions ago, or what an imaging report concluded, the doctor must still open documents and read. The knowledge is captured and indexed, yet there is no way to put a question to it and get a trustworthy, source-grounded answer back.

The hard part is not producing an answer — a general model will happily produce one. The hard part is producing an answer a doctor can *trust*: one drawn only from this patient's real record, that says "I don't have that" instead of inventing when the record is silent, and that points at the exact document behind every claim. An assistant that sounds confident but is ungrounded is worse than none.

## Solution

Two new capabilities on the existing service, built over the Chunks it already stores: **Retrieval** — finding, ranking, and packaging the patient's own Chunks relevant to a question as Evidence Items with provenance — and **grounded answering** — turning a question plus that evidence into a cited clinical Grounded Answer, or an honest Insufficient Evidence refusal.

A doctor's question (relayed by the existing backend) is scoped to the patient, optionally refined to resolve pronouns from prior turns, embedded with the same model that indexed the record, and matched against the patient's Chunks in the authoritative pgvector store. Chunks that clear a Confidence Threshold become Evidence Items; if none do, the assistant returns a deterministic Insufficient Evidence answer rather than guessing. Otherwise a single grounded clinical answer is generated in the question's language, every claim cited to `[E#]` evidence, and the answer is **verified against that evidence before it is returned** — nothing ungrounded ever leaves the service. Retrieval is cross-language, so a Greek question can be answered from an English lab report and vice versa.

This service stays stateless about chat: conversations, message history, memory, and titles belong to the backend and are out of scope. This PRD covers **retrieval and stateless grounded answering only**, and calibration of the Confidence Threshold and result count is deferred to the golden test sets (T36).

## User Stories

1. As a doctor, I want to ask a natural-language question about a patient and get an answer drawn from that patient's record, so that I can recall what the record holds without re-reading every document.
2. As a doctor, I want every factual claim in an answer cited to the specific document it came from, so that I can verify it and trust it clinically.
3. As a doctor, I want each citation to include a short verbatim quote and the source location, so that I can confirm the answer reflects what the document actually says.
4. As a doctor, I want to be told plainly "this patient's record does not contain information to answer that" when the record is silent, so that I never mistake absence of evidence for a real answer.
5. As a doctor, I want the assistant to never answer a medical question from the model's own general knowledge, so that every clinical statement is grounded in *this* patient's material.
6. As a doctor, I want answers grounded across all document types — transcripts, my notes, lab renditions, imaging findings — so that nothing in the record is invisible to a question.
7. As a doctor, I want to ask a follow-up question using pronouns or references ("what about her cholesterol?") and be understood, so that a conversation feels natural.
8. As a doctor, I want to ask in Greek or English and get the answer in the same language, so that I can work in whichever language I am thinking in.
9. As a doctor, I want a Greek question to still find an English lab report about the same patient (and vice versa), so that a mixed-language record is never a barrier to an answer.
10. As a doctor, I want the answer to name the clinical date of the evidence behind it, so that I know *when* something was recorded, not just that it was.
11. As a doctor, I want to be certain an answer can only ever draw on the correct patient's record, so that a patient's information can never leak into another patient's answer.
12. As a doctor, I want to optionally narrow a question to one doctor's documents, while the default is the patient's whole record, so that I can focus when I need to without losing the full picture by default.
13. As a doctor, I want a document I un-ingested or corrected to never surface in an answer, so that removed or superseded material cannot mislead me later.
14. As a doctor, I want the assistant to fail cleanly rather than show me a partial or unverified answer, so that I am never handed a claim the system could not actually ground.
15. As a doctor, I want a complete answer with its citations returned in one response, so that I can read the answer and check its sources together.
16. As a doctor, I want a question about a patient with no record yet to tell me there is nothing to answer from, so that an empty record is handled honestly rather than fabricated around.
17. As a doctor, I want the assistant's answering style to be one consistent, concise clinical voice, so that answers are predictable and easy to read.
18. As a doctor, I want the answer to acknowledge when the record only partially supports my question, citing what it can, so that I see exactly how far the evidence goes.
19. As a doctor, I want a garbled or empty question rejected clearly, so that I get a useful error instead of a meaningless answer.
20. As a patient, I want retrieval to honor un-ingestion and erasure of my data, so that anything removed can never reappear in an answer about me.
21. As a patient, I want answers about me to be confined to my own record, so that my information stays isolated and cannot surface elsewhere.
22. As a compliance officer, I want chat activity to be observable (which patient was queried, the outcome, how many citations) without any question or answer text in telemetry, so that we can audit usage without creating new exposure of patient content.
23. As a compliance officer, I want all AI processing for chat (query refinement, embedding, answer generation) confined to the same EU-region, DPA-covered services as ingestion, so that GDPR obligations continue to hold for the chat path.

## Implementation Decisions

Vocabulary throughout is the project glossary ([CONTEXT.md](../../CONTEXT.md), *Retrieval & Chat* section); the load-bearing choices are recorded as ADRs 0010–0012 and the design record ([retrieval-and-chat-design.md](../retrieval-and-chat-design.md)), summarized here.

### Engineering constraints

These are restrictions on *how* the chat path is built, so a clinic-facing assistant stays trustworthy and the service stays narrow:

- **The service stays stateless about chat.** No conversation, message, memory, or title storage is added here; the grounded-answer path is a pure function of its inputs (same patient, question, evidence in → same answer out).
- **The trust boundary from ADR-0007 is not widened.** Still one caller (the backend), still no per-user token; `patientId`/`doctorId` travel in the request and are trusted.
- **Grounding is enforced by code, not hoped for in a prompt.** The Confidence Threshold, Insufficient-Evidence refusal, and Citation verification are deterministic policy around the model, not instructions the model may ignore.
- **Retrieval quality is measurable.** The Confidence Threshold and result count are configuration calibrated against a fixed corpus (T36), not hard-coded lore.
- **AI providers remain configuration, not architecture.** Refinement, embedding, and answer generation reach models through the same provider abstractions as ingestion; the embedding model must match the one that indexed the record.
- **Telemetry carries no patient text.** Chat observability records ids and counts only, extending the existing observability boundary (ADR-0009).

### Decisions

- **Service boundary (ADR-0010).** Retrieval and stateless grounded answering live in this service; conversations, message persistence, idempotency, Conversation Memory, and titles belong to the backend and are out of scope. The grounded-answer endpoint *accepts* bounded Conversation Context as input but stores none of it. Conversation Context is used only to interpret and phrase — it is never an evidence source, and every medical fact is re-retrieved and grounded on the current turn.
- **Public surface.** Exactly one new HTTP endpoint, `POST /patients/{patientId}/chat/answer`, backend-mediated and secret-authenticated with the existing `X-Api-Key` scheme. Retrieval itself is an internal service, not a public endpoint in v1 — it is promoted to an endpoint later only if a second consumer needs raw evidence.
- **Scope and isolation (ADR-0011).** `patientId` (in the route) is the only hard boundary and is applied *in* the search query, not as a post-filter — a retrieval that does not constrain the patient is a bug. The asking `doctorId` is carried for audit; an optional narrowing filter may restrict to one doctor's documents. Optional filters for document type, clinical-date range, session, and language are applied in the same query.
- **Single authoritative store — no hydration (ADR-0011).** pgvector *is* the system of record, so there is no separate "validate the hit against SQL" step and no overfetch: a search hit is already the authoritative Chunk row. The ANN search orders by the `halfvec(3072)` cast to use the existing HNSW index, and the query is embedded with the **same model and dimensions as ingestion** (`text-embedding-3-large`, 3072); the per-Chunk embedding-model record guards against a silent mismatch. Un-ingested/superseded Chunks are physically gone, so they cannot surface.
- **Retrieval pipeline shape.** An ordered-step registry: each stage is a registered step with an `Order`, resolved, sorted, and run in sequence over a shared request-scoped Retrieval Context — **scope → refine → embed → search → package**. Deferred stages (hybrid search, a structured-analyte tool) slot in by registration without disturbing the others.
- **Query Refinement.** Optional, configuration-gated, and fails open to the raw question. It rewrites the question into a cleaner search query using Conversation Context to resolve pronouns; it affects only the query vector, never the answer's grounding.
- **Grounded answering (ADR-0012).** A Microsoft Agent Framework agent with **database-seeded instructions** (ADR-0008) generates one grounded answer in a single fixed clinical voice — no persona subsystem — in the language of the question. It is told to answer only from the supplied `[E#]` Evidence Items and to cite them.
- **Safety policy (ADR-0012).** (a) A **Confidence Threshold** below which a hit is not treated as evidence. (b) **Insufficient-Evidence refusal**: when nothing clears the threshold — including a patient with no Chunks at all — a deterministic, code-owned message localized to the question's language is returned; no model call runs on the refusal path. (c) **Citation verification**: every `[E#]` the answer cites must have been supplied this turn and still resolve; the answer is generated in full, verified, then returned. Verification failure **fails the turn (5xx) with no corrective retry** — deliberately unlike the ingestion chunker's single retry — and never emits the unverified text.
- **Delivery.** **Non-streaming in v1**: generate → verify → return a complete answer with its Citations. A refusal is a normal `200` outcome (`refused: true`, empty citations), not an error. An unknown/empty patient takes this same refusal path — never a `404` — so patient existence is not leaked and no patient registry is assumed. Blank/garbled questions are `400`; missing/invalid secret is `401`; provider failures after transient retries are `5xx`.
- **Observability (extends ADR-0009).** Chat emits spans (refine / embed / search / generate / verify) and metrics (answered vs. refused, citation count, latency) carrying only ids and counts — never question or answer text. No chat rows are persisted; the who-asked-what user audit belongs to the backend, which holds the user identity.
- **Contract (decision-encoding sketch, trimmed).** The request/response shape encodes the boundary decisions above more precisely than prose:

  ```json
  // POST /patients/{patientId}/chat/answer   — request
  {
    "doctorId": "…",                       // asking doctor: audit only
    "question": "…",
    "recentTurns": [{ "role": "user|assistant", "text": "…" }],  // optional, bounded
    "priorSummary": "…",                   // optional, bounded (backend memory)
    "topK": 8,                              // optional; clamped 1..50
    "filters": {                            // all optional
      "doctorId": "…",                     // narrow to one doctor's documents
      "documentType": "…", "from": "…", "to": "…", "sessionId": "…", "language": "el|en"
    }
  }
  // response (200 for both an answer and a refusal)
  {
    "answer": "…", "refused": false, "retrievalUsed": true, "language": "el",
    "citations": [{ "label": "E1", "chunkId": "…", "documentId": "…", "documentType": "…",
                    "documentDate": "…", "sourceRef": "…", "quote": "…bounded verbatim…", "score": 0.83 }]
  }
  ```

## Testing Decisions

- **Test external behavior only.** Tests assert on the response a caller sees — `answer`, `refused`, `retrievalUsed`, `language`, and the `citations` (their `chunkId`/provenance/`score`/order) — never on internal step wiring, prompt text, or private types. A test should survive an internal refactor of the pipeline.
- **One behavioral seam: the HTTP endpoint.** All feature tests cross `POST /patients/{patientId}/chat/answer` through the existing in-process host against a **real pgvector container** (the established `IngestionApiFixture` pattern), with the existing scripted `IChatClient` fake driving refinement and answer generation deterministically. Chunks under test are created by ingesting Documents through the existing pipeline, so retrieval runs against real stored Chunks. The internal retrieval service is dropped to as a seam only if a specific behavior genuinely cannot be expressed at the endpoint.
- **One new test seam: a controllable embedding fake.** The current deterministic embedding fake derives vectors from a hash, so it has no semantic structure — adequate for ingestion but not for asserting similarity ranking or the Confidence-Threshold cutoff. A controllable fake that lets a test pin known vectors to known texts makes ranking and threshold-boundary behavior deterministic. This is the only new seam the feature introduces.
- **Modules tested.** The grounded-answer endpoint and, through it, the retrieval pipeline, the safety policy (threshold, refusal, verification), refinement fail-open, and scope/isolation filtering.
- **Representative behaviors to cover.** Patient isolation (another patient's Chunks never appear); optional `doctorId`/document-type/date/session/language filters; cross-language retrieval; ranking order and threshold cutoff (via the controllable fake); Insufficient-Evidence refusal, including the empty/unknown-patient path, as a `200`; deterministic refusal text per language; citation-verification failure → `5xx` with nothing emitted; answer written in the question's language; refinement disabled/failing falls back to the raw question; un-ingested/superseded Chunks absent from answers; blank question → `400`; missing secret → `401`.
- **Prior art.** The suite's ~132 existing integration tests (real pgvector via Testcontainers, `ScriptedChatClient`, `DeterministicEmbeddingGenerator`), which already exercise ingestion end-to-end through the public interface — the same fixture, fakes, and assertion style are reused here.

## Out of Scope

- **All conversation state**: conversations, message history and states, idempotency, Conversation Memory (rolling conversation summary + recent turns as *stored* state), and auto-titles — these belong to the backend (ADR-0010). This service only *accepts* Conversation Context as input.
- **The backend itself**, and any doctor-app-facing surface — the backend does not exist yet and is out of scope.
- **Live token streaming** of the answer — v1 is non-streaming (ADR-0012); streaming, if wanted, is added at the backend's channel later.
- **Structured analyte trend answering** (e.g. "HbA1c over the year") — a separate later tool over `analyte_results` with its own merge/ranking policy; v1 retrieval is vector-over-Chunks only.
- **Hybrid lexical/keyword search** for exact drug names, lab codes, and terminology — a deferred recall improvement, revisited if pure vector recall proves inadequate against the golden set.
- **Source-authority hierarchy and freshness/expiry policy** — not applicable to a single-patient corpus; "freshness" is simply the clinical `DocumentDate` surfaced on citations.
- **Personas / multiple answering styles** — one fixed clinical voice in v1.
- **A public retrieval endpoint** — retrieval is internal in v1.
- **Final Confidence-Threshold and `topK` values** — calibrated against the golden test sets (T36), not fixed by this PRD.

## Further Notes

- **Design provenance.** Adapted from the Consultant-AI retrieval blueprint (`docs/helping_documents_out_of_repo/helping_project/docs/medical-assistant-retrieval/`), corrected for this service's single-store (pgvector-is-authoritative), single-trusted-caller, SignalR, and single-patient-corpus realities. The divergences are recorded in ADR-0011 and the design record.
- **Invariant inversion.** Ingestion's invariant is "the AI points, code copies" — no generated patient text is stored (ADR-0002/0006). Chat inverts this: the answer *is* generated prose. ADR-0012 restates the invariant for chat — the model may assert only what the evidence supports, and the service proves it before release.
- **Calibration ties to T36.** The Confidence Threshold, `topK`, prompt-in-context count, and whether hybrid search is needed are all open by design and are meant to be tuned against curated Greek/English/mixed cases — making T36 a natural predecessor or companion to implementation.
- **Publication note.** This PRD is filed as a repository document under `docs/prd/` with the `ready-for-agent` label in frontmatter, matching PRD 0001. The repository has no GitHub issues and no `ready-for-agent` GitHub label; if a GitHub issue is also wanted, it can be created on request.
