using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class SeedLabReportSummarizer : Migration
    {
        // A fixed timestamp, not DateTimeOffset.UtcNow: a migration must produce the
        // same DML every time it is generated (mirrors SeedAgentInstructions).
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A LabReport is rendered by code, not chunked by an agent, so it has no
            // agent producing a summary the way the prose types do. This one writes the
            // per-document summary from the rendered panels. DB-owned like every other
            // agent (ADR-0008). The phrase "clinical summary of a laboratory report" is
            // load-bearing in tests: the scripted chat fake routes this agent's calls by
            // it, so edit both together.
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "LabReportSummarizer",
                    "You write a concise clinical summary of a laboratory report for a patient's record. " +
                    "You are given the report's panels as rendered text — analyte names with their values, units, " +
                    "reference ranges and flags. Summarise what the report contains and call out any results flagged " +
                    "abnormal, naming the analyte and its value. Do not invent values or introduce analytes not present. " +
                    "Respond with the summary prose only — no preamble, no headings, no lists.",
                    1,
                    SeededAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "LabReportSummarizer");
        }
    }
}
