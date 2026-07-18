using Microsoft.EntityFrameworkCore;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// EF Core context for the single Postgres database that holds both ingestion
/// state and the vector store (ADR-0001) — one database so a Correction can
/// supersede chunks and flip status in one transaction. Embedding dimensions
/// come from configuration (<c>Embeddings:Dimensions</c>).
/// </summary>
public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options, IConfiguration configuration)
    : DbContext(options)
{
    /// <summary>Durable Ingestion records (status, content hash, raw payload).</summary>
    public DbSet<IngestionRecord> Ingestions => Set<IngestionRecord>();

    /// <summary>The vector store: verbatim chunks with embeddings and the metadata spine.</summary>
    public DbSet<Chunk> Chunks => Set<Chunk>();

    /// <summary>Per-agent system instructions, seeded from code defaults (ADR-0008).</summary>
    public DbSet<AgentInstruction> AgentInstructions => Set<AgentInstruction>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        var embeddingDimensions = configuration.GetValue("Embeddings:Dimensions", 3072);

        modelBuilder.Entity<IngestionRecord>(entity =>
        {
            entity.ToTable("ingestions");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).HasColumnName("id");
            entity.Property(i => i.DocumentType).HasColumnName("document_type");
            entity.Property(i => i.DoctorId).HasColumnName("doctor_id");
            entity.Property(i => i.PatientId).HasColumnName("patient_id");
            entity.Property(i => i.SessionId).HasColumnName("session_id");
            entity.Property(i => i.SequenceNumber).HasColumnName("sequence_number");
            entity.Property(i => i.Status).HasColumnName("status");
            entity.Property(i => i.ErrorMessage).HasColumnName("error_message");
            entity.Property(i => i.Attempts).HasColumnName("attempts");
            entity.Property(i => i.ContentHash).HasColumnName("content_hash");
            entity.Property(i => i.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(i => i.InstructionVersion).HasColumnName("instruction_version");
            entity.Property(i => i.ChatModel).HasColumnName("chat_model");
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
            entity.Property(c => c.Embedding).HasColumnName("embedding").HasColumnType($"vector({embeddingDimensions})");
            entity.HasOne<IngestionRecord>().WithMany().HasForeignKey(c => c.IngestionId);
            entity.HasIndex(c => c.IngestionId);
        });
    }
}
