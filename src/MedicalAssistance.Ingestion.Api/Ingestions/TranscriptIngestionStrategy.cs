namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Ingestion Strategy for SessionTranscript documents. A doctor–patient
/// transcript is dialog, so its chunks are stamped <c>dialog</c>; otherwise it is
/// the shared prose pipeline (boundaries-only chunking, verbatim assembly, batched
/// embedding, atomic store) driven by the TranscriptChunker instructions.
/// </summary>
public sealed class TranscriptIngestionStrategy(ProseIngestionPipeline pipeline) : IIngestionStrategy
{
    /// <inheritdoc />
    public string DocumentType => DocumentTypes.SessionTranscript;

    /// <inheritdoc />
    public Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct) =>
        pipeline.RunAsync(
            ingestionId,
            request,
            body: request.Transcript!,
            bodyChunkKind: "dialog",
            agentInstructionName: AgentNames.TranscriptChunker,
            promptHeader: "Transcript lines:",
            ct);
}
