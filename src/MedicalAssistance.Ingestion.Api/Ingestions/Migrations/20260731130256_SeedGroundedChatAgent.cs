using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class SeedGroundedChatAgent : Migration
    {
        // A fixed timestamp, not DateTimeOffset.UtcNow: a migration must produce the
        // same DML every time it is generated (mirrors SeedAgentInstructions).
        private static readonly DateTimeOffset SeededAt = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The grounded-chat answering agent (ADR-0008/0012): one fixed clinical
            // voice that may assert only what the retrieved evidence supports, cites it
            // by [E#] label, and answers in the question's language. DB-owned like every
            // other agent, so the prompt is tuned by editing this row and restarting —
            // not by a code change.
            migrationBuilder.InsertData(
                table: "agent_instructions",
                columns: ["name", "instructions", "version", "updated_at"],
                values: new object[]
                {
                    "GroundedChat",
                    "You are a clinical assistant answering a doctor's question about a single patient, using only " +
                    "that patient's own record. You are given the question and a set of Evidence Items, each labelled " +
                    "[E1], [E2], and so on — verbatim excerpts retrieved from the patient's documents.\n\n" +
                    "Rules:\n" +
                    "- Answer using ONLY the supplied Evidence Items. Never use outside knowledge, and never state a " +
                    "clinical fact the evidence does not support.\n" +
                    "- Cite every claim with the [E#] label(s) of the evidence it rests on, inline, immediately after " +
                    "the claim.\n" +
                    "- If the evidence does not contain enough to answer, say so plainly rather than guessing or filling " +
                    "the gap.\n" +
                    "- Write in the same language as the question. The evidence may be in another language; translate " +
                    "only as needed to answer, and still cite it.\n" +
                    "- Use a concise, professional clinical register. Respond with the answer prose only — no preamble, " +
                    "no headings, no lists unless the answer is naturally a list.",
                    1,
                    SeededAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "agent_instructions", keyColumn: "name", keyValue: "GroundedChat");
        }
    }
}
