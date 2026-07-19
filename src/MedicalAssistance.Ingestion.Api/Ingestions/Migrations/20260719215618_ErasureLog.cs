using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <summary>
    /// Adds the erasure_log table — the append-only audit GDPR Erasure writes and
    /// nothing ever deletes from. A plain new table, so this body is scaffolded as
    /// generated.
    /// </summary>
    public partial class ErasureLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erasure_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<string>(type: "text", nullable: false),
                    erased_by = table.Column<string>(type: "text", nullable: false),
                    erased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ingestions_erased = table.Column<int>(type: "integer", nullable: false),
                    chunks_erased = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erasure_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_erasure_log_patient_id",
                table: "erasure_log",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erasure_log");
        }
    }
}
