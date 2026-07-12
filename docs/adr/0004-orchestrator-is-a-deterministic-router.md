# The ingestion Orchestrator is a deterministic router, not an agent

Despite the "agentic ingestion" framing, the top-level Orchestrator is a registry lookup: declared Document Type → Ingestion Strategy. Every upload must carry its Document Type as metadata; the system never infers type from content. AI agents live inside strategies (e.g., the transcript strategy's chunking agent, built as a Microsoft Agent Framework ChatClientAgent), not above them.

Why: uploads always know their type (the doctor's app has distinct flows per document kind), so LLM-based routing would add nondeterminism, latency, and cost to every ingestion for zero information gain — and a silent misclassification in a medical pipeline sends a document down the wrong path with no one watching. Routing is auditable and testable as a dictionary; intelligence is spent where content actually needs understanding.

## Consequences

- New document types (lab results, doctor's notes) are added as new strategies with their own stages, agents, and identity/dedup rules; the platform layer never hard-wires transcript identity (`sessionId`, `sequenceNumber`).
- The pipeline itself is plain sequential C# (an ordered list of stages), not a MAF Workflow graph — a workflow engine for a straight line fights readability. If a future strategy genuinely needs dynamic multi-agent coordination, MAF Workflows are the designated upgrade path inside that strategy.
- If genuinely untyped documents ever appear, a classifier agent may be added as a front stage with a confidence threshold that routes "unsure" to a human — never to a guess.
