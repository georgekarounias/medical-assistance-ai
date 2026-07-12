# An Ingestion is atomic: it either fully committed or it reruns from scratch

There is no stage checkpointing. Nothing an ingestion produces (chunks, vectors) is visible until the final single Postgres transaction (delete superseded chunks + insert new chunks + status = Completed). On crash or retry, the pipeline reruns entirely — re-chunk, re-embed, re-store.

Why: "either it fully happened or it didn't" is the simplest failure story developers can reason about, and it falls out of the transactional store for free. Checkpointing intermediate outputs would add a persistence schema, per-stage resume logic, and stale-checkpoint bugs to save fractions of a cent per rare crash.

## Consequences

- Crash recovery is just: on startup, re-enqueue non-terminal Ingestion records (attempt-capped to avoid poison loops).
- A retried ingestion may produce different chunk boundaries than the lost attempt (LLM nondeterminism). That is acceptable: nothing from the lost attempt was ever visible.
- The raw Document payload is persisted on the Ingestion record, because rerun-from-scratch and the retry endpoint require the original input. This makes the service a system of record for PHI — it inherits the estate's encryption-at-rest and backup rules.
