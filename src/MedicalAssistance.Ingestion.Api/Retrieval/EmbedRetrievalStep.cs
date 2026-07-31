using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The Embed step (Order 30): turns the effective query into the ANN probe vector.
/// It uses the very same <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> the
/// ingestion pipeline embedded chunks with — one DI registration — so the query
/// vector is produced by the same model and dimensions that indexed the record.
/// That shared seam is what keeps a query from silently probing with a different
/// model than the chunks were written with (ADR-0011).
/// </summary>
public sealed class EmbedRetrievalStep(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : IRetrievalStep
{
    public int Order => RetrievalStepOrder.Embed;

    public async Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken)
    {
        var embeddings = await embeddingGenerator.GenerateAsync(
            [context.EffectiveQuery], cancellationToken: cancellationToken);
        context.QueryEmbedding = embeddings[0].Vector.ToArray();
    }
}
