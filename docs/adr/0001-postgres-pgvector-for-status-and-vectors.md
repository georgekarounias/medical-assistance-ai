# Postgres + pgvector holds both ingestion state and vectors

We need an ingestion-status store (durable job records, content hashes) and a vector store (chunks + embeddings). We chose a single Postgres database with pgvector for both, instead of a dedicated vector DB (Qdrant, Azure AI Search) plus a relational DB.

Why: at clinic scale (thousands of vectors/day, not millions) pgvector with HNSW is more than sufficient, and one database makes a Correction transactional — deleting superseded chunks, inserting new ones, and flipping ingestion status commit atomically, so the RAG chat can never observe a half-ingested or half-corrected transcript. One system to operate, one backup story.

## Consequences

- Vector access goes through `Microsoft.Extensions.VectorData` abstractions, so a later move to a dedicated vector DB is a connector/config change — but it would forfeit the transactional supersede, which would then need a saga/cleanup design. That cost is the real lock-in, not the connector.
- Do not "upgrade" to a dedicated vector DB for performance without measuring; the atomicity is worth more than latency at this scale.
