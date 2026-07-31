using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Tests.Fakes;

/// <summary>
/// Controllable variant of the embedding seam, for retrieval tests. A test pins a
/// known vector to a known text, so cosine distances — and therefore ranking order
/// and the confidence-threshold cutoff — are exactly what the test dictates. Any
/// text without a pin gets a stable, densely-spread vector derived from its bytes
/// (reproducible across runs and processes, unlike <see cref="string.GetHashCode()"/>),
/// so unpinned filler never accidentally out-ranks a deliberately placed chunk.
///
/// The hash-based <see cref="DeterministicEmbeddingGenerator"/> stays for the many
/// ingestion tests that never assert on similarity; this one exists only where
/// ordering or refusal is the thing under test.
/// </summary>
public sealed class ControllableEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    // A test usually pins during setup and reads during ingestion on worker
    // threads, so keep the map concurrent. Ordinal keying: the pin must match the
    // exact string later handed to GenerateAsync, byte for byte.
    private readonly ConcurrentDictionary<string, float[]> _pinned = new(StringComparer.Ordinal);

    /// <summary>The model name this fake reports — stamped on every chunk it embeds.</summary>
    public const string ModelId = "controllable-test-embedding-model";

    /// <summary>
    /// Pins <paramref name="text"/> to a vector whose leading dimensions are
    /// <paramref name="leadingComponents"/> and whose remaining dimensions are zero.
    /// Direction is all cosine distance reads, so a couple of components are enough
    /// to place a text precisely relative to others. Returns <c>this</c> so pins
    /// chain. The key must equal the exact string later passed to
    /// <see cref="GenerateAsync"/>.
    /// </summary>
    public ControllableEmbeddingGenerator Pin(string text, params float[] leadingComponents)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(leadingComponents);
        if (leadingComponents.Length > dimensions)
            throw new ArgumentException(
                $"Pinned vector has {leadingComponents.Length} components but the generator is {dimensions}-dimensional.",
                nameof(leadingComponents));
        if (Array.TrueForAll(leadingComponents, c => c == 0f))
            throw new ArgumentException(
                "A pinned vector needs at least one non-zero component; cosine distance is undefined for the zero vector.",
                nameof(leadingComponents));

        var vector = new float[dimensions];
        Array.Copy(leadingComponents, vector, leadingComponents.Length);
        _pinned[text] = vector;
        return this;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values
            .Select(value => new Embedding<float>(
                _pinned.TryGetValue(value, out var pinned) ? (float[])pinned.Clone() : Derive(value)))
            .ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    // A stable dense vector for any unpinned text: an FNV-1a hash over the UTF-8
    // bytes seeds an xorshift PRNG, giving a reproducible spread across every
    // dimension. Dense and roughly orthogonal to the sparse pinned vectors, so
    // unpinned filler never crowds the ranking a test set up.
    private float[] Derive(string value)
    {
        var state = Fnv1a(value);
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            // Map to [-1, 1); spreading the sign keeps vectors off a single quadrant.
            vector[i] = state / (float)ulong.MaxValue * 2f - 1f;
        }
        return vector;
    }

    private static ulong Fnv1a(string value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }
        // xorshift stalls on a zero seed; the empty string hashes to the basis anyway.
        return hash == 0 ? offsetBasis : hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("controllable", defaultModelId: ModelId)
            : null;

    public void Dispose()
    {
    }
}
