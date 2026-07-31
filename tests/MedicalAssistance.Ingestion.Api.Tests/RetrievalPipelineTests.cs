using MedicalAssistance.Ingestion.Api.Retrieval;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The retrieval pipeline skeleton (T40): a request-scoped <see cref="RetrievalContext"/>
/// and an ordered-step registry. Each step is an <see cref="IRetrievalStep"/> with an
/// <see cref="IRetrievalStep.Order"/>; the service sorts them and runs them in sequence
/// against the shared context. The first step establishes the mandatory patient_id scope
/// — the security boundary — before anything else touches the request.
///
/// These are direct tests of the internal service: T40 deliberately exposes no HTTP
/// surface (that is T42), so the seam under test is <see cref="IRetrievalService"/> itself.
/// Embedding and the vector search land in T41, so the pipeline here ends at an empty
/// evidence set.
/// </summary>
public class RetrievalPipelineTests
{
    private static RetrievalRequest AnyRequest(RetrievalFilters? filters = null) => new()
    {
        PatientId = "patient-42",
        Question = "Is the patient diabetic?",
        DoctorId = "dr-asking",
        Filters = filters ?? new RetrievalFilters(),
    };

    [Fact]
    public async Task Steps_run_in_ascending_order_regardless_of_registration_order()
    {
        var log = new List<int>();
        // Registered deliberately out of order; the service must sort by Order.
        var service = new RetrievalService([
            new RecordingStep(50, log),
            new RecordingStep(10, log),
            new RecordingStep(40, log),
            new RecordingStep(20, log),
        ]);

        await service.SearchAsync(AnyRequest());

        Assert.Equal([10, 20, 40, 50], log);
    }

    [Fact]
    public async Task The_scope_step_establishes_the_patient_id_boundary_before_later_steps_run()
    {
        RetrievalScope? scopeSeenByLaterStep = null;
        // Later step registered first, to prove the scope is set by ordering, not luck.
        var service = new RetrievalService([
            new RecordingStep(RetrievalStepOrder.Search, [], ctx => scopeSeenByLaterStep = ctx.Scope),
            new ScopeRetrievalStep(),
        ]);

        await service.SearchAsync(AnyRequest());

        Assert.NotNull(scopeSeenByLaterStep);
        Assert.Equal("patient-42", scopeSeenByLaterStep!.PatientId);
    }

    [Fact]
    public async Task The_scope_carries_the_optional_narrowing_filters()
    {
        RetrievalScope? scope = null;
        var service = new RetrievalService([
            new ScopeRetrievalStep(),
            new RecordingStep(RetrievalStepOrder.Search, [], ctx => scope = ctx.Scope),
        ]);

        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await service.SearchAsync(AnyRequest(new RetrievalFilters
        {
            DoctorId = "dr-narrow",
            DocumentType = "LabReport",
            From = from,
            To = to,
            SessionId = "sess-9",
            Language = "en",
        }));

        Assert.NotNull(scope);
        Assert.Equal("patient-42", scope!.PatientId);
        Assert.Equal("dr-narrow", scope.DoctorId);
        Assert.Equal("LabReport", scope.DocumentType);
        Assert.Equal(from, scope.From);
        Assert.Equal(to, scope.To);
        Assert.Equal("sess-9", scope.SessionId);
        Assert.Equal("en", scope.Language);
    }

    [Fact]
    public async Task A_blank_patient_id_is_a_bug_the_scope_step_refuses_to_run()
    {
        // patient_id is the one hard boundary (ADR-0011); a retrieval without it is a
        // bug, not a slow path. The scope step is where that invariant is enforced.
        var service = new RetrievalService([new ScopeRetrievalStep()]);
        var request = new RetrievalRequest { PatientId = "  ", Question = "anything", DoctorId = "dr" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(request));
    }

    [Fact]
    public async Task The_skeleton_returns_an_empty_evidence_set_until_search_is_added()
    {
        // No search step yet (T41), so the pipeline runs and produces nothing —
        // the honest state of the skeleton.
        var service = new RetrievalService([new ScopeRetrievalStep()]);

        var result = await service.SearchAsync(AnyRequest());

        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task The_embed_step_probes_with_the_same_generator_ingestion_used()
    {
        // The Embed step must produce the query vector from the same seam that embedded
        // the chunks — that shared instance is the "same model/dimensions" guarantee.
        // Here the generator has the question pinned, so the probe is exactly that vector.
        var embeddings = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions)
            .Pin("Is the patient diabetic?", 0.7f, 0.3f, 0.1f);
        var context = new RetrievalContext(AnyRequest());

        await new EmbedRetrievalStep(embeddings).ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(context.QueryEmbedding);
        Assert.Equal(IngestionApiFixture.EmbeddingDimensions, context.QueryEmbedding!.Length);
        Assert.Equal(0.7f, context.QueryEmbedding[0]);
        Assert.Equal(0.3f, context.QueryEmbedding[1]);
        Assert.Equal(0.1f, context.QueryEmbedding[2]);
    }

    // Records the order it ran at, and optionally inspects the shared context — the
    // stand-in for the real steps T41+ register.
    private sealed class RecordingStep(int order, IList<int> log, Action<RetrievalContext>? inspect = null) : IRetrievalStep
    {
        public int Order => order;

        public Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken)
        {
            log.Add(order);
            inspect?.Invoke(context);
            return Task.CompletedTask;
        }
    }
}
