using System.Globalization;
using System.Text;
using MedicalAssistance.Ingestion.Api.Ingestions;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The Search step (Order 40): the ANN scan over the authoritative <c>chunks</c>
/// store, scoped and packaged. It reads the patient boundary and filters the Scope
/// step set, the probe vector the Embed step produced, and returns the nearest
/// chunks as Evidence Items — score, provenance, verbatim text (ADR-0011).
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
public sealed class SearchRetrievalStep(NpgsqlDataSource dataSource) : IRetrievalStep
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

        // Parameters are built in positional order: $1 patient (the boundary), $2 the
        // probe vector (referenced in both the SELECT distance and the ORDER BY), then
        // each present filter, then the LIMIT. Every value is a bound parameter — no
        // scope value is ever concatenated into the SQL.
        var parameters = new List<object> { scope.PatientId, ToVectorLiteral(queryEmbedding) };
        var where = new StringBuilder("patient_id = $1");

        AppendFilter(where, parameters, "doctor_id", "=", scope.DoctorId);
        AppendFilter(where, parameters, "document_type", "=", scope.DocumentType);
        AppendFilter(where, parameters, "session_id", "=", scope.SessionId);
        AppendFilter(where, parameters, "language", "=", scope.Language);
        AppendFilter(where, parameters, "document_date", ">=", scope.From);
        AppendFilter(where, parameters, "document_date", "<=", scope.To);

        parameters.Add(topK);
        var limitParam = $"${parameters.Count}";

        var sql =
            $"""
            SELECT id, document_id, document_type, chunk_index, session_id, document_date,
                   language, chunk_kind, source_ref, verbatim_text,
                   embedding::{HalfVecCast} <=> $2::{HalfVecCast} AS distance
            FROM chunks
            WHERE {where}
            ORDER BY embedding::{HalfVecCast} <=> $2::{HalfVecCast}
            LIMIT {limitParam}
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in parameters)
            command.Parameters.Add(new NpgsqlParameter { Value = value });

        var evidence = new List<EvidenceItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var distance = reader.GetDouble(10);
            evidence.Add(new EvidenceItem
            {
                ChunkId = reader.GetGuid(0),
                DocumentId = reader.GetString(1),
                DocumentType = reader.GetString(2),
                ChunkIndex = reader.GetInt32(3),
                SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                DocumentDate = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                Language = reader.IsDBNull(6) ? null : reader.GetString(6),
                ChunkKind = reader.GetString(7),
                SourceRef = reader.IsDBNull(8) ? null : reader.GetString(8),
                VerbatimText = reader.GetString(9),
                // Cosine distance → similarity, so a bigger score is a closer hit.
                Score = 1.0 - distance,
            });
        }

        context.Evidence = evidence;
    }

    private static void AppendFilter(
        StringBuilder where, List<object> parameters, string column, string op, object? value)
    {
        if (value is null)
            return;
        parameters.Add(value);
        where.Append($" AND {column} {op} ${parameters.Count}");
    }

    // pgvector accepts a bracketed, comma-separated literal, cast to halfvec in SQL —
    // the same shape the ingestion path stores and the HNSW index is built over. "R"
    // round-trips each float exactly so the probe is bit-for-bit the embedded query.
    private static string ToVectorLiteral(float[] vector) =>
        "[" + string.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
