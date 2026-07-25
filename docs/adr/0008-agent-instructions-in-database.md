# Agent instructions live in the database, loaded once at startup

Each AI agent's instructions (the TranscriptChunker system prompt today; the DoctorNoteChunker now, the lab column mapper and future agents tomorrow) are stored in an agent_instructions table rather than hardcoded. On application start they are loaded once into a singleton provider; strategies build their agents from it. The instruction *text* lives only in the database — code holds no runtime default copy of it, only the agent-name keys used to look it up. A fresh database is seeded by an EF Core migration (SeedAgentInstructions), so the starting prompts are version-controlled at the database layer and a fresh boot still comes up with them.

Why: prompt wording is tuning, not architecture — it will be adjusted per environment and per model (Greek quality, new deployments) far more often than code ships. Database + restart gives operational tunability without a redeploy, while loading once into a singleton keeps the hot path free of database reads and the running system deterministic (no mid-flight prompt drift between two ingestions).

## Consequences

- A prompt change requires an application restart to take effect — deliberate: cheaper than a redeploy, but still an explicit, observable act.
- Prompt text edited in the database bypasses git history; the migration that seeds a fresh database is the reviewed reference version, and once a row exists the table is authoritative — the migration never overwrites a later operator edit.
- Seeding is a migration rather than an application-startup loop, so it is serialized by the migration lock and recorded in the migration history: a concurrent rolling deploy against a fresh database inserts each row exactly once, with no application-side seeding code to keep correct (this is what closes bug B17).
- Instructions carry a version, and every ingestion records the instruction version and chat model that processed it — so a quality regression is traceable to the prompt that caused it, and only its ingestions need re-processing.
- Safety does not move: the output-contract guardrails (boundary validation ADR-0002, verbatim verification ADR-0006) are code, so no prompt edit can make the pipeline store altered patient data — a bad prompt can only make ingestions fail honestly.
