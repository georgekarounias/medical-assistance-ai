using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class PatientRollingSummary : Migration
    {
        // A fixed timestamp, not DateTimeOffset.UtcNow: a migration must produce the
        // same DML every time it is generated (mirrors SeedAgentInstructions).
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patient_summaries",
                columns: table => new
                {
                    patient_id = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    document_count = table.Column<int>(type: "integer", nullable: false),
                    chat_model = table.Column<string>(type: "text", nullable: true),
                    instruction_version = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_summaries", x => x.patient_id);
                });

            // The patient-summariser prompt, DB-owned like every other agent (ADR-0008),
            // so it can be edited and reloaded on restart without a code change.
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "PatientSummarizer",
                    "You maintain a single rolling clinical overview of one patient. " +
                    "You are given dated summaries of every document currently held for the patient, oldest first. " +
                    "Write one concise, coherent overview of the patient across all of them: the ongoing conditions, " +
                    "the course over time, and anything a doctor should know at a glance. " +
                    "Prefer the most recent information when documents conflict, and note meaningful changes over time. " +
                    "Do not invent facts that are not supported by the summaries. Respond with the overview prose only — no preamble, no headings.",
                    1,
                    SeededAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "PatientSummarizer");

            migrationBuilder.DropTable(
                name: "patient_summaries");
        }
    }
}
