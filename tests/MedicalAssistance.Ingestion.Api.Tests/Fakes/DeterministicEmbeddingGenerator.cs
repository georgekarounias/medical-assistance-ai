using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Tests.Fakes;

/// <summary>
/// Adapter for the IEmbeddingGenerator seam: derives a small deterministic
/// vector from the input text, so embeddings are stable across runs.
/// </summary>
public sealed class DeterministicEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values
            .Select(value =>
            {
                var vector = new float[dimensions];
                for (var i = 0; i < dimensions; i++)
                    vector[i] = ((value.GetHashCode() * (i + 31)) % 997) / 997f;
                return new Embedding<float>(vector);
            })
            .ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
