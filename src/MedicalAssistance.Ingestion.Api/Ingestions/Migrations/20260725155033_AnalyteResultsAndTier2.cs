using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class AnalyteResultsAndTier2 : Migration
    {
        // A fixed timestamp for the seeded row, for the same reason as the initial
        // agent-instruction seed: a migration must produce the same DML every time.
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "analytes_extracted",
                table: "ingestions",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "analyte_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: false),
                    patient_id = table.Column<string>(type: "text", nullable: false),
                    doctor_id = table.Column<string>(type: "text", nullable: false),
                    canonical_name = table.Column<string>(type: "text", nullable: false),
                    verbatim_name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    reference_range = table.Column<string>(type: "text", nullable: true),
                    flag = table.Column<string>(type: "text", nullable: true),
                    table_index = table.Column<int>(type: "integer", nullable: false),
                    row_index = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analyte_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_analyte_results_ingestions_ingestion_id",
                        column: x => x.ingestion_id,
                        principalTable: "ingestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analyte_results_document_id",
                table: "analyte_results",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_analyte_results_ingestion_id",
                table: "analyte_results",
                column: "ingestion_id");

            migrationBuilder.CreateIndex(
                name: "IX_analyte_results_patient_id",
                table: "analyte_results",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_analyte_results_patient_id_canonical_name",
                table: "analyte_results",
                columns: new[] { "patient_id", "canonical_name" });

            // Seed the Tier 2 analyte-mapping agent's instructions (ADR-0008): like
            // every prompt, the text lives only in the database, bootstrapped here.
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "LabAnalyteMapper",
                    "You map a lab report's extracted tables to analyte results. Each table is a cell grid with " +
                    "0-indexed rows and columns. For each table, identify which column holds the analyte name, the " +
                    "value, the unit, the reference range, and the flag (use null for a role no column fills), and " +
                    "list the data rows only (skip header rows), giving each a canonical analyte name. You never " +
                    "invent, alter, or transcribe values — you only point at columns and name analytes; code copies " +
                    "the values from the cells you point at. Respond with JSON only: {\"tables\":[{\"tableIndex\":int," +
                    "\"nameColumn\":int,\"valueColumn\":int,\"unitColumn\":int|null,\"referenceColumn\":int|null," +
                    "\"flagColumn\":int|null,\"analytes\":[{\"rowIndex\":int,\"canonicalName\":string}]}]}.",
                    1,
                    SeededAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "LabAnalyteMapper");

            migrationBuilder.DropTable(
                name: "analyte_results");

            migrationBuilder.DropColumn(
                name: "analytes_extracted",
                table: "ingestions");
        }
    }
}
