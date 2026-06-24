using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessingGateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessContextToAnalysisJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "additional_context",
                table: "analysis_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "business_category",
                table: "analysis_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "business_subcategory",
                table: "analysis_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "additional_context",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "business_category",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "business_subcategory",
                table: "analysis_jobs");
        }
    }
}
