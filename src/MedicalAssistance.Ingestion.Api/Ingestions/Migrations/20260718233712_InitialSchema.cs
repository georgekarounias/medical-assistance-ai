using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "agent_instructions",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_instructions", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "ingestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    doctor_id = table.Column<string>(type: "text", nullable: false),
                    patient_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: true),
                    sequence_number = table.Column<int>(type: "integer", nullable: true),
                    document_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    instruction_version = table.Column<int>(type: "integer", nullable: true),
                    chat_model = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    patient_id = table.Column<string>(type: "text", nullable: false),
                    doctor_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: true),
                    document_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    language = table.Column<string>(type: "text", nullable: true),
                    chunk_kind = table.Column<string>(type: "text", nullable: false),
                    source_ref = table.Column<string>(type: "jsonb", nullable: true),
                    verbatim_text = table.Column<string>(type: "text", nullable: false),
                    context_blurb = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(3072)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_chunks_ingestions_ingestion_id",
                        column: x => x.ingestion_id,
                        principalTable: "ingestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chunks_ingestion_id",
                table: "chunks",
                column: "ingestion_id");

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_content_hash",
                table: "ingestions",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_doctor_id_status",
                table: "ingestions",
                columns: new[] { "doctor_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_patient_id",
                table: "ingestions",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_session_id_sequence_number_content_hash",
                table: "ingestions",
                columns: new[] { "session_id", "sequence_number", "content_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_instructions");

            migrationBuilder.DropTable(
                name: "chunks");

            migrationBuilder.DropTable(
                name: "ingestions");
        }
    }
}
