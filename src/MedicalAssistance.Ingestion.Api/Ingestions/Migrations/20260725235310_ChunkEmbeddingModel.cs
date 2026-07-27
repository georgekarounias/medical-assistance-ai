using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalAssistance.Ingestion.Api.Ingestions.Migrations
{
    /// <inheritdoc />
    public partial class ChunkEmbeddingModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "embedding_model",
                table: "chunks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding_model",
                table: "chunks");
        }
    }
}
