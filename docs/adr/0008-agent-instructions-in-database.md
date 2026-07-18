# Agent instructions live in the database, loaded once at startup

Each AI agent's instructions (the TranscriptChunker system prompt today; the lab column mapper and future agents tomorrow) are stored in an agent_instructions table rather than hardcoded. On application start they are loaded once into a singleton provider; strategies build their agents from it. Code-side defaults seed the table when a row is missing, so a fresh database still boots.

Why: prompt wording is tuning, not architecture — it will be adjusted per environment and per model (Greek quality, new deployments) far more often than code ships. Database + restart gives operational tunability without a redeploy, while loading once into a singleton keeps the hot path free of database reads and the running system deterministic (no mid-flight prompt drift between two ingestions).

## Consequences

- A prompt change requires an application restart to take effect — deliberate: cheaper than a redeploy, but still an explicit, observable act.
- Prompt text in the database bypasses git history; the seeded defaults in source remain the reviewed reference version, and the table is the override.
- Instructions carry a version, and every ingestion records the instruction version and chat model that processed it — so a quality regression is traceable to the prompt that caused it, and only its ingestions need re-processing.
- Safety does not move: the output-contract guardrails (boundary validation ADR-0002, verbatim verification ADR-0006) are code, so no prompt edit can make the pipeline store altered patient data — a bad prompt can only make ingestions fail honestly.
