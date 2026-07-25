using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <summary>
    /// Seeds the agent instructions into the database (ADR-0008). The instructions
    /// are owned by the <c>agent_instructions</c> table, not by a runtime code
    /// default: this migration is the version-controlled bootstrap that gives a
    /// fresh database its starting prompts, after which the row is authoritative and
    /// an operator edits it directly (a restart reloads the singleton).
    ///
    /// Seeding as a migration rather than an application-startup loop means it is
    /// serialized by the migration lock and recorded in the migration history, so a
    /// rolling deploy of several instances against a fresh database inserts each row
    /// exactly once — the duplicate-key race B17 guarded against cannot arise, with
    /// no application-side seeding code to keep correct.
    ///
    /// A data-only migration: it declares no schema, so the model snapshot is
    /// unchanged and the model does not drift from its migrations.
    /// </summary>
    public partial class SeedAgentInstructions : Migration
    {
        // A fixed timestamp, not DateTimeOffset.UtcNow: a migration must produce the
        // same DDL/DML every time it is generated, and this is only the initial
        // value — an operator edit sets its own updated_at.
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "TranscriptChunker",
                    "You segment doctor-patient session transcripts into topically coherent chunks. " +
                    "You only return line boundaries and descriptions — never transcript text. " +
                    "Respond with JSON only: {\"chunks\":[{\"startLine\":int,\"endLine\":int,\"contextBlurb\":string}],\"summary\":string}. " +
                    "Boundaries are inclusive, contiguous, non-overlapping, and must cover every line.",
                    1,
                    SeededAt,
                });

            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "DoctorNoteChunker",
                    "You segment a doctor's clinical note about a patient into topically coherent chunks. " +
                    "The text is the doctor's own monologue, not a dialogue. " +
                    "You only return line boundaries and descriptions — never note text. " +
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
                table: "agent_instructions", keyColumn: "name", keyValue: "TranscriptChunker");
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "DoctorNoteChunker");
        }
    }
}
