using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <summary>
    /// Rewrites stored document ids from "sessionId#sequenceNumber" to
    /// "doctorId#patientId#sessionId#sequenceNumber".
    ///
    /// The schema does not change here — only the values in it — so this body is
    /// hand-written: scaffolding sees nothing to do. It has to exist all the
    /// same. A Correction finds the version it replaces by document id, so
    /// chunks left under the old form would never be matched, and both versions
    /// of a transcript would stay in the patient's record for retrieval to quote
    /// against each other.
    /// </summary>
    public partial class DocumentIdCarriesDoctorAndPatient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new form is the old one with the doctor and patient prepended,
            // so the rewrite is a prefix and needs no parsing of what is there.
            migrationBuilder.Sql(
                """
                UPDATE chunks
                SET document_id = doctor_id || '#' || patient_id || '#' || document_id
                WHERE document_type = 'SessionTranscript';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the two leading components and their separators back off the
            // front. Postgres substring is 1-indexed, so the remainder starts one
            // past the second '#'.
            migrationBuilder.Sql(
                """
                UPDATE chunks
                SET document_id = substring(
                    document_id from length(doctor_id) + length(patient_id) + 3)
                WHERE document_type = 'SessionTranscript'
                  AND document_id LIKE doctor_id || '#' || patient_id || '#%';
                """);
        }
    }
}
