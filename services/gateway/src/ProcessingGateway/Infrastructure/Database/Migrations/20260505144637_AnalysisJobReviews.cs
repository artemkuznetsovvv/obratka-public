using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessingGateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisJobReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analysis_job_reviews",
                columns: table => new
                {
                    analysis_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analysis_job_reviews", x => new { x.analysis_job_id, x.review_id });
                    table.ForeignKey(
                        name: "fk_analysis_job_reviews_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analysis_job_reviews_review",
                table: "analysis_job_reviews",
                column: "review_id");

            // GRANT SELECT analytics_reader на новую таблицу — Web API Analytics-модулю
            // нужен сейчас как минимум обратный JOIN к reviews для подсчёта объёма анализа.
            // Роль создаётся init-скриптом Postgres; DO-блок защищает от падения, если её нет.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'analytics_reader') THEN
                        GRANT SELECT ON analysis_job_reviews TO analytics_reader;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_job_reviews");
        }
    }
}
