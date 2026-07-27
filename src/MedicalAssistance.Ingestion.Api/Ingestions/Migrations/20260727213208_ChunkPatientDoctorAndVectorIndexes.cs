using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class ChunkPatientDoctorAndVectorIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Metadata filter index. Every similarity search is patient-scoped
            // (the security boundary), optionally narrowed to one doctor; leading
            // with patient_id lets this one B-tree serve both "this patient" and
            // "this patient, this doctor", and keeps patient erasure (which deletes
            // chunks by patient_id) off a table scan.
            migrationBuilder.CreateIndex(
                name: "IX_chunks_patient_id_doctor_id",
                table: "chunks",
                columns: new[] { "patient_id", "doctor_id" });

            // Vector ANN index for the similarity search itself. pgvector's HNSW
            // (and IVFFlat) indexes cap at 2000 dimensions for full-precision
            // vectors, and the embedding column is vector(3072) — so the index is
            // built over a half-precision cast (halfvec, indexable to 4000 dims).
            // Cosine ops match the distance the retrieval query uses; for the index
            // to be used, that query must order by
            //   embedding::halfvec(3072) <=> :query::halfvec(3072)
            // Raw SQL because EF cannot express the cast inside an index expression.
            // Built CONCURRENTLY-free here: it runs inside the migration transaction
            // on an empty/small table at deploy time, where a brief lock is fine.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_chunks_embedding_hnsw\" ON chunks " +
                "USING hnsw ((embedding::halfvec(3072)) halfvec_cosine_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_chunks_embedding_hnsw\";");

            migrationBuilder.DropIndex(
                name: "IX_chunks_patient_id_doctor_id",
                table: "chunks");
        }
    }
}
