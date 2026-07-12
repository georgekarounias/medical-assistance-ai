# The chunking LLM returns turn boundaries, never chunk text

Semantic chunking is done by an LLM, but the model only returns structured output — turn index ranges plus a Context Blurb per chunk and a Transcript Summary. Code assembles each chunk's text verbatim from the original Turns. The LLM never re-emits patient dialog.

Why: a generative model that reproduces medical dialog can silently substitute words (dosages, negations) — a clinical-safety failure, not a quality bug. Boundaries-only makes that physically impossible: stored transcript text is verbatim by construction, and the only AI-generated text in the store (blurbs, summaries) is labeled as such. It is also ~10× cheaper/faster, since output is a small JSON structure instead of the whole transcript re-emitted.

## Consequences

- Code must validate the LLM's boundaries (contiguous, non-overlapping, covering all turns) and enforce size guardrails (target ~200–600 tokens, hard max ~800; merge/split at turn boundaries). One corrective retry on invalid output, then the ingestion is marked Failed (explicitly chosen over a degraded fixed-size fallback — we prefer an honest, retriable failure to silently worse retrieval).
- Embedding input is blurb + verbatim text; anything displayed to a doctor is verbatim text only.
