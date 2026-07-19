using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <summary>
    /// Adds the un-ingest surface to the ingestions table: the assembled
    /// <c>document_id</c> a delete addresses, and the <c>deleted_by</c> /
    /// <c>deleted_at</c> tombstone a delete writes. Also makes <c>payload</c>
    /// nullable, because un-ingest scrubs the raw transcript out of it.
    ///
    /// The <c>document_id</c> backfill is hand-written: scaffolding would give
    /// existing rows an empty string, but an ingestion whose id did not match its
    /// own chunks (which already carry the assembled id) could never be un-ingested.
    /// It reproduces <see cref="DocumentIdentity.For" /> for the one document type
    /// that exists, then makes the column NOT NULL once no row is missing it.
    /// </summary>
    public partial class UnIngestDocumentIdAndTombstone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "ingestions",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "ingestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by",
                table: "ingestions",
                type: "text",
                nullable: true);

            // Added nullable, backfilled, then constrained — an existing row has
            // to receive its real id before NOT NULL can hold.
            migrationBuilder.AddColumn<string>(
                name: "document_id",
                table: "ingestions",
                type: "text",
                nullable: true);

            // The same string DocumentIdentity.For builds for a transcript, and
            // the same one already stored on this row's chunks.
            migrationBuilder.Sql(
                """
                UPDATE ingestions
                SET document_id = doctor_id || '#' || patient_id || '#' || session_id || '#' || sequence_number
                WHERE document_type = 'SessionTranscript';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "document_id",
                table: "ingestions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_document_id",
                table: "ingestions",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_chunks_document_id",
                table: "chunks",
                column: "document_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ingestions_document_id",
                table: "ingestions");

            migrationBuilder.DropIndex(
                name: "IX_chunks_document_id",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "ingestions");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "ingestions");

            migrationBuilder.DropColumn(
                name: "document_id",
                table: "ingestions");

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "ingestions",
                type: "jsonb",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}
