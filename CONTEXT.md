# Clinical Document Ingestion

Ingests clinical documents about a patient into a vector store so a RAG chat can answer a doctor's questions about that patient. Supported Document Types: session transcripts, doctor notes, lab reports, imaging reports. Receives documents via HTTP POST from the existing backend; the phone app never talks to this service.

## Language

### Platform

**Document**:
Any clinical artifact about a patient submitted for ingestion. Every Document carries a declared Document Type. Transcripts, Doctor Notes, Lab Reports, and Imaging Reports are Documents.

**Document Type**:
The declared kind of a Document (SessionTranscript, DoctorNote, LabReport, ImagingReport), supplied by the uploader — never inferred. Selects the Ingestion Strategy.

**Ingestion Strategy**:
The processing pipeline for one Document Type. Each strategy defines its own stages, agents, and identity/dedup rule.

**Orchestrator**:
The deterministic router that selects the Ingestion Strategy for a Document by its Document Type. It does not make content-based decisions.
_Avoid_: Classifier, dispatcher agent

**Ingestion**:
The processing of one Document through its Ingestion Strategy. The unit of work, status tracking, and SignalR notification.
_Avoid_: Upload, import, job

**Chunk**:
A semantically coherent span of text derived from a Document. The unit of embedding and retrieval. Every Chunk carries the shared metadata spine (patient, doctor, document type/id, clinical date).
_Avoid_: Segment, passage, split

**Correction**:
A re-POST of an existing Document identity with different content. Supersedes the prior Document: its old Chunks (and any derived rows) are deleted before the new content is ingested. An identical re-POST (same content hash) is a no-op only when the prior Ingestion succeeded; after a failure it is a retry.
_Avoid_: Update, re-upload

**Un-ingest**:
Doctor-initiated removal of one Document and everything derived from it (Chunks, Analyte Results, payload). The Ingestion record remains as an audit tombstone. Distinct from Erasure.
_Avoid_: Delete (ambiguous), rollback

**Erasure**:
Admin-level GDPR removal of all data for one patient, including audit tombstones and raw payloads. Irreversible and itself logged.
_Avoid_: Un-ingest, purge

### Session Transcripts

**Session**:
A single real-world encounter between one doctor and one patient. Identified by `sessionId`, with `doctorId` and `patientId` as context.
_Avoid_: Visit, appointment, recording

**Transcript**:
One transcription artifact belonging to a Session, carrying the dialog as free text. Identified by (`sessionId`, `sequenceNumber`). A Session may have several Transcripts.
_Avoid_: Recording, document

**Sequence Number**:
The ordinal of a Transcript within its Session. A POST with a new sequence number is a Continuation; a POST reusing an existing one is a Correction.

**Line**:
One non-empty line of a Transcript's text; the atom that chunk boundaries snap to. The service never parses lines into structured speaker turns.
_Avoid_: Turn, message, utterance

**Continuation**:
A new Transcript (unseen sequence number) for an existing Session — a sibling that adds content. Its chunks coexist with those of earlier Transcripts.

**Context Blurb**:
A 1–2 sentence LLM-written description of what a prose Chunk is about, prepended to the chunk text before embedding to resolve pronouns and back-references. Used by the transcript and note strategies.
_Avoid_: Header, preamble

**Transcript Summary**:
An LLM-written summary of one whole Transcript, stored and embedded as its own retrievable Chunk.
_Avoid_: Session summary (a Session may have several Transcripts)

### Doctor Notes

**Doctor Note**:
A free-text note written by the doctor about a patient, optionally linked to a Session. Identified by `noteId` from the backend; an edited note re-POSTs the same `noteId` as a Correction.
_Avoid_: Comment, annotation

### Lab & Imaging Reports

**Lab Report**:
A digitally generated PDF of laboratory results for a patient, containing one or more Panels. Identified by a backend-assigned document id.
_Avoid_: Lab result (ambiguous with Analyte Result), test

**Panel**:
One group of measurements from a single collection — typically one table in a Lab Report. Rendered as one Chunk.
_Avoid_: Table, section

**Analyte Result**:
A single measured value in a Panel (name, value, unit, reference range, flag), copied verbatim from the report. The unit of trend queries.
_Avoid_: Lab value, measurement, fact

**Rendition**:
The deterministic, code-generated human-readable text of a Panel, built from extracted table cells. What gets embedded for a Lab Report — never LLM-written.
_Avoid_: Summary (nothing is summarized), narrative

**Imaging Report**:
The radiologist's written findings for an imaging study, arriving as a PDF. Only its text is ingested; the pixel data is never ingested, only referenced by link.
_Avoid_: Image, scan, study
