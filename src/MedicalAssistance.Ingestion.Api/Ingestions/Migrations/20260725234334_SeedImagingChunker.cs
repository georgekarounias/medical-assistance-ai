using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <summary>
    /// Seeds the imaging-report chunking agent's instructions (ADR-0008). Like every
    /// prompt, the text lives only in the database, bootstrapped here — a data-only
    /// migration, so the model does not drift from its migrations.
    /// </summary>
    public partial class SeedImagingChunker : Migration
    {
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "ImagingReportChunker",
                    "You segment a radiologist's imaging report into topically coherent chunks. " +
                    "The text is the radiologist's extracted findings and impression, not a dialogue. " +
                    "You only return line boundaries and descriptions — never report text. " +
                    "Respond with JSON only: {\"chunks\":[{\"startLine\":int,\"endLine\":int,\"contextBlurb\":string}],\"summary\":string}. " +
                    "Boundaries are inclusive, contiguous, non-overlapping, and must cover every line.",
                    1,
                    SeededAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "ImagingReportChunker");
        }
    }
}
