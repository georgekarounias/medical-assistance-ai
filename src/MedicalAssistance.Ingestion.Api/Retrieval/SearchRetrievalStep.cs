using System.Globalization;
using System.Text;
using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The Search step (Order 40): the ANN scan over the authoritative <c>chunks</c>
/// store, scoped and packaged. It reads the patient boundary and filters the Scope
/// step set, the probe vector the Embed step produced, and returns the nearest
/// chunks as Evidence Items — score, provenance, verbatim text (ADR-0011).
///
/// It runs through the same <see cref="IngestionDbContext"/> the ingestion store
/// uses, via <c>SqlQueryRaw</c>: the <c>::halfvec(3072)</c> cast that makes the
/// query hit the HNSW index cannot be expressed in LINQ, so the SQL is written by
/// hand and projected onto a small row type — but the connection, configuration and
/// materialisation stay EF's.
///
/// Two invariants are load-bearing and easy to break silently:
/// <list type="bullet">
/// <item><c>patient_id</c> is in the WHERE, never a post-filter — a search without
/// it is a bug, not a slow path.</item>
/// <item>ordering is over the <c>halfvec</c> cast the HNSW index is built on;
/// ordering by the raw <c>vector</c> would table-scan.</item>
/// </list>
/// There is no hydration and no overfetch: a hit is already the authoritative row,
/// and <c>TopK</c> is requested directly.
/// </summary>
public sealed class SearchRetrievalStep(IngestionDbContext db) : IRetrievalStep
{
    /// <summary>Bounds on how many chunks a single retrieval may pull (design record; enforced again at the endpoint, T48).</summary>
    public const int MinTopK = 1;
    public const int MaxTopK = 50;

    private static readonly string HalfVecCast = $"halfvec({IngestionDbContext.EmbeddingDimensions})";

    public int Order => RetrievalStepOrder.Search;

    public async Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken)
    {
        var scope = context.Scope
            ?? throw new InvalidOperationException("Search ran before the Scope step set the patient boundary.");
        var queryEmbedding = context.QueryEmbedding
            ?? throw new InvalidOperationException("Search ran before the Embed step produced a query vector.");

        var topK = Math.Clamp(context.Request.TopK, MinTopK, MaxTopK);

        // Named parameters, so the probe vector can be referenced twice (the SELECT
        // distance and the ORDER BY) as one @queryVec, and every scope value is bound
        // rather than concatenated into the SQL. patient_id is the boundary and is
        // always present; each filter is added only when set.
        var parameters = new List<NpgsqlParameter>
        {
            new("patientId", scope.PatientId),
            new("queryVec", ToVectorLiteral(queryEmbedding)),
            new("topK", topK),
        };
        var where = new StringBuilder("patient_id = @patientId");

        AppendFilter(where, parameters, "doctor_id", "=", "doctorId", scope.DoctorId);
        AppendFilter(where, parameters, "document_type", "=", "documentType", scope.DocumentType);
        AppendFilter(where, parameters, "session_id", "=", "sessionId", scope.SessionId);
        AppendFilter(where, parameters, "language", "=", "language", scope.Language);
        AppendFilter(where, parameters, "document_date", ">=", "fromDate", scope.From);
        AppendFilter(where, parameters, "document_date", "<=", "toDate", scope.To);

        // Columns are aliased to the row type's property names (quoted, so Postgres
        // keeps their casing); source_ref is cast to text so the jsonb comes back as
        // a plain string.
        var sql =
            $"""
            SELECT id AS "ChunkId", document_id AS "DocumentId", document_type AS "DocumentType",
                   chunk_index AS "ChunkIndex", session_id AS "SessionId", document_date AS "DocumentDate",
                   language AS "Language", chunk_kind AS "ChunkKind", source_ref::text AS "SourceRef",
                   verbatim_text AS "VerbatimText",
                   embedding::{HalfVecCast} <=> @queryVec::{HalfVecCast} AS "Distance"
            FROM chunks
            WHERE {where}
            ORDER BY embedding::{HalfVecCast} <=> @queryVec::{HalfVecCast}
            LIMIT @topK
            """;

        var rows = await db.Database
            .SqlQueryRaw<EvidenceRow>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);

        context.Evidence = rows
            .Select(row => new EvidenceItem
            {
                ChunkId = row.ChunkId,
                DocumentId = row.DocumentId,
                DocumentType = row.DocumentType,
                ChunkIndex = row.ChunkIndex,
                SessionId = row.SessionId,
                DocumentDate = row.DocumentDate,
                Language = row.Language,
                ChunkKind = row.ChunkKind,
                SourceRef = row.SourceRef,
                VerbatimText = row.VerbatimText,
                // Cosine distance → similarity, so a bigger score is a closer hit.
                Score = 1.0 - row.Distance,
            })
            .ToList();
    }

    private static void AppendFilter(
        StringBuilder where, List<NpgsqlParameter> parameters, string column, string op, string paramName, object? value)
    {
        if (value is null)
            return;
        parameters.Add(new NpgsqlParameter(paramName, value));
        where.Append($" AND {column} {op} @{paramName}");
    }

    // pgvector accepts a bracketed, comma-separated literal, cast to halfvec in SQL —
    // the same shape the ingestion path stores and the HNSW index is built over. "R"
    // round-trips each float exactly so the probe is bit-for-bit the embedded query.
    private static string ToVectorLiteral(float[] vector) =>
        "[" + string.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    // The shape one chunks row projects onto — SqlQueryRaw materialises the aliased
    // columns onto these properties. Distance is pgvector's cosine distance; the step
    // turns it into the similarity Score on the Evidence Item.
    private sealed record EvidenceRow(
        Guid ChunkId,
        string DocumentId,
        string DocumentType,
        int ChunkIndex,
        string? SessionId,
        DateTimeOffset? DocumentDate,
        string? Language,
        string ChunkKind,
        string? SourceRef,
        string VerbatimText,
        double Distance);
}
