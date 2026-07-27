using Microsoft.EntityFrameworkCore;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// EF Core context for the single Postgres database that holds both ingestion
/// state and the vector store (ADR-0001) — one database so a Correction can
/// supersede chunks and flip status in one transaction.
/// </summary>
public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// Width of the stored embedding vectors, and therefore of the
    /// <c>vector(n)</c> column itself.
    ///
    /// A constant rather than a setting: the dimension is part of the schema,
    /// so once migrations own the schema it cannot vary per environment without
    /// the two disagreeing. Moving to an embedding model of a different size is
    /// a migration that alters the column and re-embeds what is stored — not a
    /// configuration change, because existing vectors do not resize.
    /// </summary>
    public const int EmbeddingDimensions = 3072;

    /// <summary>Durable Ingestion records (status, content hash, raw payload).</summary>
    public DbSet<IngestionRecord> Ingestions => Set<IngestionRecord>();

    /// <summary>The vector store: verbatim chunks with embeddings and the metadata spine.</summary>
    public DbSet<Chunk> Chunks => Set<Chunk>();

    /// <summary>Per-agent system instructions, seeded from code defaults (ADR-0008).</summary>
    public DbSet<AgentInstruction> AgentInstructions => Set<AgentInstruction>();

    /// <summary>The append-only audit of GDPR erasures — the one thing an erasure leaves behind.</summary>
    public DbSet<ErasureLogEntry> ErasureLog => Set<ErasureLogEntry>();

    /// <summary>Verified LabReport analyte rows (Tier 2), stored relationally beside the vector chunks.</summary>
    public DbSet<AnalyteResult> AnalyteResults => Set<AnalyteResult>();

    /// <summary>One rolling overview per patient, regenerated after each ingestion.</summary>
    public DbSet<PatientSummary> PatientSummaries => Set<PatientSummary>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<IngestionRecord>(entity =>
        {
            entity.ToTable("ingestions");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).HasColumnName("id");
            entity.Property(i => i.DocumentId).HasColumnName("document_id");
            entity.Property(i => i.DocumentType).HasColumnName("document_type");
            entity.Property(i => i.DoctorId).HasColumnName("doctor_id");
            entity.Property(i => i.PatientId).HasColumnName("patient_id");
            entity.Property(i => i.SessionId).HasColumnName("session_id");
            entity.Property(i => i.SequenceNumber).HasColumnName("sequence_number");
            entity.Property(i => i.DocumentDate).HasColumnName("document_date");
            entity.Property(i => i.Status).HasColumnName("status");
            entity.Property(i => i.ErrorMessage).HasColumnName("error_message");
            entity.Property(i => i.DeletedBy).HasColumnName("deleted_by");
            entity.Property(i => i.DeletedAt).HasColumnName("deleted_at");
            entity.Property(i => i.Attempts).HasColumnName("attempts");
            entity.Property(i => i.ContentHash).HasColumnName("content_hash");
            entity.Property(i => i.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(i => i.Summary).HasColumnName("summary");
            entity.Property(i => i.InstructionVersion).HasColumnName("instruction_version");
            entity.Property(i => i.ChatModel).HasColumnName("chat_model");
            entity.Property(i => i.AnalytesExtracted).HasColumnName("analytes_extracted");
            entity.Property(i => i.CreatedAt).HasColumnName("created_at");
            entity.Property(i => i.UpdatedAt).HasColumnName("updated_at");

            // Every submission asks two questions before anything durable
            // happens — "has this exact content already been sent for this
            // identity?" and "has it been sent for this patient at all?" — so
            // neither may degrade into a scan of the table.
            entity.HasIndex(i => new { i.SessionId, i.SequenceNumber, i.ContentHash });
            entity.HasIndex(i => i.ContentHash);

            // The resync query: one doctor's unfinished work, asked on every
            // reconnect, against a table that only ever grows.
            entity.HasIndex(i => new { i.DoctorId, i.Status });

            // The patient document list, and every patient-scoped operation
            // that follows it.
            entity.HasIndex(i => i.PatientId);

            // Un-ingest addresses an ingestion by its assembled document id, and
            // supersede/duplicate detection could match on it too — a document's
            // rows should be findable without a scan.
            entity.HasIndex(i => i.DocumentId);
        });

        modelBuilder.Entity<AgentInstruction>(entity =>
        {
            entity.ToTable("agent_instructions");
            entity.HasKey(a => a.Name);
            entity.Property(a => a.Name).HasColumnName("name");
            entity.Property(a => a.Instructions).HasColumnName("instructions");
            entity.Property(a => a.Version).HasColumnName("version");
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ErasureLogEntry>(entity =>
        {
            entity.ToTable("erasure_log");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PatientId).HasColumnName("patient_id");
            entity.Property(e => e.ErasedBy).HasColumnName("erased_by");
            entity.Property(e => e.ErasedAt).HasColumnName("erased_at");
            entity.Property(e => e.IngestionsErased).HasColumnName("ingestions_erased");
            entity.Property(e => e.ChunksErased).HasColumnName("chunks_erased");

            // A compliance query asks what became of one patient.
            entity.HasIndex(e => e.PatientId);
        });

        modelBuilder.Entity<Chunk>(entity =>
        {
            entity.ToTable("chunks");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).HasColumnName("id");
            entity.Property(c => c.IngestionId).HasColumnName("ingestion_id");
            entity.Property(c => c.ChunkIndex).HasColumnName("chunk_index");
            entity.Property(c => c.DocumentId).HasColumnName("document_id");
            entity.Property(c => c.DocumentType).HasColumnName("document_type");
            entity.Property(c => c.PatientId).HasColumnName("patient_id");
            entity.Property(c => c.DoctorId).HasColumnName("doctor_id");
            entity.Property(c => c.SessionId).HasColumnName("session_id");
            entity.Property(c => c.DocumentDate).HasColumnName("document_date");
            entity.Property(c => c.Language).HasColumnName("language");
            entity.Property(c => c.ChunkKind).HasColumnName("chunk_kind");
            entity.Property(c => c.SourceRef).HasColumnName("source_ref").HasColumnType("jsonb");
            entity.Property(c => c.VerbatimText).HasColumnName("verbatim_text");
            entity.Property(c => c.ContextBlurb).HasColumnName("context_blurb");
            entity.Property(c => c.Embedding).HasColumnName("embedding")
                .HasColumnType($"vector({EmbeddingDimensions})");
            entity.Property(c => c.EmbeddingModel).HasColumnName("embedding_model");
            entity.HasOne<IngestionRecord>().WithMany().HasForeignKey(c => c.IngestionId);
            entity.HasIndex(c => c.IngestionId);

            // Both supersede and un-ingest delete a document's chunks by this id.
            entity.HasIndex(c => c.DocumentId);

            // The retrieval spine: every similarity search is patient-scoped (the
            // security boundary), optionally narrowed to one doctor. Leading with
            // patient_id makes this one index serve both "this patient" and "this
            // patient, this doctor", and it also keeps patient erasure — which
            // deletes chunks by patient_id — off a table scan. The vector ANN index
            // (HNSW over a halfvec cast) is added by raw SQL in the migration, since
            // the 3072-dim embedding exceeds pgvector's 2000-dim full-precision HNSW
            // limit and EF cannot express the cast.
            entity.HasIndex(c => new { c.PatientId, c.DoctorId });
        });

        modelBuilder.Entity<PatientSummary>(entity =>
        {
            entity.ToTable("patient_summaries");
            entity.HasKey(p => p.PatientId);
            entity.Property(p => p.PatientId).HasColumnName("patient_id");
            entity.Property(p => p.Summary).HasColumnName("summary");
            entity.Property(p => p.DocumentCount).HasColumnName("document_count");
            entity.Property(p => p.ChatModel).HasColumnName("chat_model");
            entity.Property(p => p.InstructionVersion).HasColumnName("instruction_version");
            entity.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AnalyteResult>(entity =>
        {
            entity.ToTable("analyte_results");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.IngestionId).HasColumnName("ingestion_id");
            entity.Property(a => a.DocumentId).HasColumnName("document_id");
            entity.Property(a => a.PatientId).HasColumnName("patient_id");
            entity.Property(a => a.DoctorId).HasColumnName("doctor_id");
            entity.Property(a => a.CanonicalName).HasColumnName("canonical_name");
            entity.Property(a => a.VerbatimName).HasColumnName("verbatim_name");
            entity.Property(a => a.Value).HasColumnName("value");
            entity.Property(a => a.Unit).HasColumnName("unit");
            entity.Property(a => a.ReferenceRange).HasColumnName("reference_range");
            entity.Property(a => a.Flag).HasColumnName("flag");
            entity.Property(a => a.TableIndex).HasColumnName("table_index");
            entity.Property(a => a.RowIndex).HasColumnName("row_index");
            entity.HasOne<IngestionRecord>().WithMany().HasForeignKey(a => a.IngestionId);

            // Supersede and un-ingest remove a document's analyte rows by this id, in
            // the same transaction as its chunks; a trend query filters by patient.
            entity.HasIndex(a => a.DocumentId);
            entity.HasIndex(a => a.PatientId);
            entity.HasIndex(a => a.IngestionId);

            // The queries these rows exist for: one analyte's values for one patient,
            // over time — "HbA1c over the year".
            entity.HasIndex(a => new { a.PatientId, a.CanonicalName });
        });
    }
}
