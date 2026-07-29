using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class IngestionQualityReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ingestion_quality_reports",
                columns: table => new
                {
                    ingestion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    token_counts = table.Column<int[]>(type: "integer[]", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    min_tokens = table.Column<int>(type: "integer", nullable: false),
                    max_tokens = table.Column<int>(type: "integer", nullable: false),
                    guardrail_merges = table.Column<int>(type: "integer", nullable: false),
                    guardrail_splits = table.Column<int>(type: "integer", nullable: false),
                    corrective_retry_fired = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_quality_reports", x => x.ingestion_id);
                    table.ForeignKey(
                        name: "FK_ingestion_quality_reports_ingestions_ingestion_id",
                        column: x => x.ingestion_id,
                        principalTable: "ingestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingestion_quality_reports");
        }
    }
}
