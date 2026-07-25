namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Ingestion Strategy for DoctorNote documents: a typed clinical note the
/// doctor wrote about a patient. A note is monologue rather than dialog, so its
/// chunks are stamped <c>note</c>; otherwise it is the same shared prose pipeline
/// as a transcript, driven by the DoctorNoteChunker instructions.
///
/// Its identity is <c>noteId</c> (carried in the assembled document id), so a
/// re-POST of the same noteId with different text is a Correction that supersedes,
/// exactly as a transcript's (sessionId, sequenceNumber) re-POST is. A note may
/// carry an optional sessionId to link it to an encounter, but its identity does
/// not depend on one — which is why identity is matched on the document id and
/// never on the raw session columns (bug B09).
/// </summary>
public sealed class DoctorNoteStrategy(ProseIngestionPipeline pipeline) : IIngestionStrategy
{
    /// <inheritdoc />
    public string DocumentType => DocumentTypes.DoctorNote;

    /// <inheritdoc />
    public Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct) =>
        pipeline.RunAsync(
            ingestionId,
            request,
            body: request.Text!,
            bodyChunkKind: "note",
            agentInstructionName: AgentNames.DoctorNoteChunker,
            promptHeader: "Note lines:",
            ct);
}
