using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The one new test seam retrieval introduces (T39): a controllable embedding
/// generator. Retrieval's whole job is similarity ordering and a threshold, so a
/// test must be able to place texts at exact distances from a query. These are
/// pure unit tests over the fake's contract — no pgvector container — because the
/// property under test is the fake itself: that pinned vectors make cosine
/// ranking, and therefore the refusal cutoff, deterministic. Everything ordering-
/// sensitive downstream (T40+) leans on this holding.
/// </summary>
public class ControllableEmbeddingGeneratorTests
{
    // Small, readable space for the ordering assertions; direction is all cosine
    // distance reads, so two components are enough to place texts precisely.
    private const int Dims = 8;

    [Fact]
    public async Task A_pinned_text_returns_exactly_its_pinned_vector()
    {
        var generator = new ControllableEmbeddingGenerator(Dims).Pin("insulin dose?", 1f, 0f);

        var vector = (await generator.GenerateAsync(["insulin dose?"]))[0].Vector.ToArray();

        Assert.Equal(Dims, vector.Length);
        Assert.Equal(1f, vector[0]);
        Assert.Equal(0f, vector[1]);
        Assert.All(vector[2..], component => Assert.Equal(0f, component));
    }

    [Fact]
    public async Task An_unpinned_text_is_stable_across_separate_generators()
    {
        // Two independent instances — the stand-in for two test runs or processes.
        // string.GetHashCode is randomised per process, so this is the property the
        // existing hash fake cannot promise and the reason this seam exists.
        var first = (await new ControllableEmbeddingGenerator(Dims).GenerateAsync(["unpinned note"]))[0].Vector.ToArray();
        var second = (await new ControllableEmbeddingGenerator(Dims).GenerateAsync(["unpinned note"]))[0].Vector.ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Different_unpinned_texts_get_different_vectors()
    {
        var generator = new ControllableEmbeddingGenerator(Dims);

        var a = (await generator.GenerateAsync(["one text"]))[0].Vector.ToArray();
        var b = (await generator.GenerateAsync(["another text"]))[0].Vector.ToArray();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Batch_generation_preserves_input_order_mixing_pinned_and_unpinned()
    {
        var generator = new ControllableEmbeddingGenerator(Dims)
            .Pin("first", 1f, 0f)
            .Pin("third", 0f, 1f);

        var vectors = await generator.GenerateAsync(["first", "second", "third"]);

        Assert.Equal(1f, vectors[0].Vector.ToArray()[0]);   // pinned "first"
        Assert.Equal(1f, vectors[2].Vector.ToArray()[1]);   // pinned "third"
        // "second" is unpinned and simply sits between them, undisturbed.
        Assert.Equal(3, vectors.Count);
    }

    [Fact]
    public async Task Pinned_vectors_make_cosine_ranking_and_the_threshold_cutoff_deterministic()
    {
        // A query and three chunks placed at deliberate angles to it: near, middling,
        // orthogonal. This is exactly what retrieval ranks and what the confidence
        // threshold cuts against, reproduced without the database.
        var generator = new ControllableEmbeddingGenerator(Dims)
            .Pin("Is the patient on insulin?", 1f, 0f)
            .Pin("Patient is on insulin daily.", 0.9f, 0.1f)     // near
            .Pin("Blood pressure was 120/80.", 0.3f, 0.95f)      // middling
            .Pin("Notes about the waiting room.", 0f, 1f);       // orthogonal

        var query = (await generator.GenerateAsync(["Is the patient on insulin?"]))[0].Vector.ToArray();
        var near = (await generator.GenerateAsync(["Patient is on insulin daily."]))[0].Vector.ToArray();
        var middling = (await generator.GenerateAsync(["Blood pressure was 120/80."]))[0].Vector.ToArray();
        var orthogonal = (await generator.GenerateAsync(["Notes about the waiting room."]))[0].Vector.ToArray();

        var dNear = CosineDistance(query, near);
        var dMiddling = CosineDistance(query, middling);
        var dOrthogonal = CosineDistance(query, orthogonal);

        // Ranking is fully determined: near < middling < orthogonal, the order
        // pgvector's `<=>` would return.
        Assert.True(dNear < dMiddling, $"near {dNear} should rank before middling {dMiddling}");
        Assert.True(dMiddling < dOrthogonal, $"middling {dMiddling} should rank before orthogonal {dOrthogonal}");

        // And the cutoff is meaningful: a threshold of 0.1 keeps only the near chunk,
        // dropping the rest — the shape of an insufficient-evidence refusal (T45).
        const double threshold = 0.1;
        Assert.True(dNear < threshold, $"near {dNear} should clear the {threshold} threshold");
        Assert.True(dMiddling > threshold, $"middling {dMiddling} should fall below the {threshold} threshold");
        Assert.True(dOrthogonal > threshold, $"orthogonal {dOrthogonal} should fall below the {threshold} threshold");
    }

    [Fact]
    public async Task It_produces_full_width_vectors_at_the_real_schema_dimension()
    {
        // The schema fixes vector(3072); a fake that produced anything else would be
        // rejected by the column, so prove it fills the real width.
        var generator = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);

        var vector = (await generator.GenerateAsync(["any patient text"]))[0].Vector.ToArray();

        Assert.Equal(IngestionApiFixture.EmbeddingDimensions, vector.Length);
    }

    [Fact]
    public void It_reports_the_model_metadata_the_committer_stamps_on_chunks()
    {
        // DocumentChunkCommitter reads EmbeddingGeneratorMetadata.DefaultModelId and
        // stamps it on every chunk; retrieval later matches query-model to chunk-model.
        var generator = new ControllableEmbeddingGenerator(Dims);

        var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;

        Assert.NotNull(metadata);
        Assert.Equal(ControllableEmbeddingGenerator.ModelId, metadata!.DefaultModelId);
    }

    [Fact]
    public void Pinning_the_zero_vector_is_rejected()
    {
        var generator = new ControllableEmbeddingGenerator(Dims);

        Assert.Throws<ArgumentException>(() => { generator.Pin("nowhere", 0f, 0f); });
    }

    [Fact]
    public void Pinning_more_components_than_dimensions_is_rejected()
    {
        var generator = new ControllableEmbeddingGenerator(2);

        Assert.Throws<ArgumentException>(() => { generator.Pin("too wide", 1f, 0f, 0f); });
    }

    // Mirrors pgvector's cosine distance (`<=>`): 1 - cosine similarity, so a
    // smaller number is a closer match — the ordering retrieval sorts by.
    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return 1.0 - dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
