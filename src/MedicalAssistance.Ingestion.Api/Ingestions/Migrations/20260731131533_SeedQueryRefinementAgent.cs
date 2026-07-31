using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class SeedQueryRefinementAgent : Migration
    {
        // A fixed timestamp, not DateTimeOffset.UtcNow: a migration must produce the
        // same DML every time it is generated (mirrors SeedAgentInstructions).
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The query-refinement agent (T44): rewrites a conversational, pronoun-heavy
            // question into a standalone search query, using the conversation context to
            // resolve references. It affects only the query vector, never the answer.
            // DB-owned like every other agent (ADR-0008); the step that uses it is
            // config-gated and fails open to the raw question, so this prompt is a recall
            // aid, not a hard dependency.
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "QueryRefinement",
                    "You rewrite a doctor's question into a single, standalone search query for a vector search over " +
                    "one patient's clinical record. Use the conversation context to resolve pronouns and references " +
                    "(\"it\", \"that\", \"the same\") into explicit clinical terms, so the query stands on its own. Keep " +
                    "the patient's own wording where it is already specific; do not add facts, diagnoses, or terms the " +
                    "question and context do not contain. Output ONLY the rewritten query text — no quotes, no preamble, " +
                    "no explanation.",
                    1,
                    SeededAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "QueryRefinement");
        }
    }
}
