# The chunking LLM returns line boundaries, never chunk text

Semantic chunking is done by an LLM, but the model only returns structured output — line index ranges plus a Context Blurb per chunk and a Transcript Summary. The transcript arrives as free text; code numbers its non-empty Lines, and assembles each chunk's text verbatim from the original Lines the model pointed at. The LLM never re-emits patient dialog. (Originally designed around structured speaker turns; the contract was later loosened to free text, so the atom became the Line — the safety architecture is unchanged.)

Why: a generative model that reproduces medical dialog can silently substitute words (dosages, negations) — a clinical-safety failure, not a quality bug. Boundaries-only makes that physically impossible: stored transcript text is verbatim by construction, and the only AI-generated text in the store (blurbs, summaries) is labeled as such. It is also ~10× cheaper/faster, since output is a small JSON structure instead of the whole transcript re-emitted.

## Consequences

- Code must validate the LLM's boundaries (contiguous, non-overlapping, covering all lines) and enforce size guardrails (target ~200–600 tokens, hard max ~800; merge/split at line boundaries). One corrective retry on invalid output, then the ingestion is marked Failed (explicitly chosen over a degraded fixed-size fallback — we prefer an honest, retriable failure to silently worse retrieval).
- Embedding input is blurb + verbatim text; anything displayed to a doctor is verbatim text only.
- Chunk quality is only as good as the transcript's line structure: the service cannot guarantee a chunk never splits mid-utterance, since it no longer receives structured turns.
