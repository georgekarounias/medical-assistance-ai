# PDFs are parsed to text via Azure Document Intelligence; pixel data is never ingested

Lab Reports and Imaging Reports arrive as digitally generated PDFs from many different providers. We extract their content with Azure Document Intelligence (layout model), which returns text plus tables as cell grids. The vector store holds only text (verbatim extractions and deterministic Renditions); actual image files (X-rays, scan pixel data) are never ingested — an Imaging Report chunk carries a link to the image in the doctor's existing viewer.

Why: Document Intelligence handles many layouts with one API, preserves table structure (naive PDF text extraction shreds lab tables), runs in EU regions under the same compliance umbrella as our Azure OpenAI usage, and puts no generative model near the numbers — extracted values are read, not generated. Ingesting pixels would mean vision models, DICOM handling, and large storage for content a text RAG cannot quote.

## Consequences

- Scanned/photographed documents (no text layer, handwriting) are explicitly out of scope; Document Intelligence can OCR them, but quality must be re-validated before that scope is opened.
- Extraction quality must be spot-checked per new lab provider, since layouts vary.
- Provider lock-in is shallow: the extraction stage is one interface; the stored artifacts (text, cell grids) are provider-neutral.
