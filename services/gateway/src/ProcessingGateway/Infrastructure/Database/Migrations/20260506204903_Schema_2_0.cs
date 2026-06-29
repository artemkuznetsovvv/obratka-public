using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcessingGateway.Infrastructure.Database.Migrations
{
    /// <summary>
    /// Schema 2.0 — переход PG-стороны на новый LLM-контракт.
    ///
    /// Изменения:
    ///   review_llm_results:
    ///     - удалены: fake_status / fake_reason_tags / sentiment / sentiment_confidence /
    ///                is_spam / spam_confidence / topics (со старым GIN)
    ///     - добавлены: overall_sentiment / overall_confidence / aspects (jsonb + GIN)
    ///   analysis_jobs:
    ///     - удалены: result_url / recommendation
    ///     - добавлены: result_reviews_url / result_summary_url / summary / recommendations_count
    ///   analysis_recommendations: новая таблица 1:N к analysis_jobs.
    ///
    /// Note: миграция **разрушительная** — пишется через DROP + ADD, потому что rename-ы
    /// с переинтерпретацией смысла поля (fake_status → overall_sentiment) сломали бы любые
    /// существующие данные. На MVP volume сносится (docker compose down -v) — данных нет.
    /// </summary>
    public partial class Schema_2_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Старый GIN-индекс по topics — надо снести ДО drop column.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_review_llm_results_topics_gin;");

            // === review_llm_results: drop old, add new ===
            migrationBuilder.DropColumn(name: "fake_status",           table: "review_llm_results");
            migrationBuilder.DropColumn(name: "fake_reason_tags",      table: "review_llm_results");
            migrationBuilder.DropColumn(name: "sentiment",             table: "review_llm_results");
            migrationBuilder.DropColumn(name: "sentiment_confidence",  table: "review_llm_results");
            migrationBuilder.DropColumn(name: "is_spam",               table: "review_llm_results");
            migrationBuilder.DropColumn(name: "spam_confidence",       table: "review_llm_results");
            migrationBuilder.DropColumn(name: "topics",                table: "review_llm_results");

            migrationBuilder.AddColumn<string>(
                name: "overall_sentiment",
                table: "review_llm_results",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "overall_confidence",
                table: "review_llm_results",
                type: "double precision",
                nullable: false,
                defaultValue: 0d);

            migrationBuilder.AddColumn<string>(
                name: "aspects",
                table: "review_llm_results",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            // GIN-индекс по новому aspects для фильтра «отзывы с темой X».
            migrationBuilder.Sql(
                "CREATE INDEX ix_review_llm_results_aspects_gin ON review_llm_results USING GIN (aspects);");

            // === analysis_jobs: drop old, add new ===
            migrationBuilder.DropColumn(name: "result_url",     table: "analysis_jobs");
            migrationBuilder.DropColumn(name: "recommendation", table: "analysis_jobs");

            migrationBuilder.AddColumn<string>(
                name: "result_reviews_url",
                table: "analysis_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result_summary_url",
                table: "analysis_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "analysis_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recommendations_count",
                table: "analysis_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // === analysis_recommendations: новая таблица ===
            migrationBuilder.CreateTable(
                name: "analysis_recommendations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    analysis_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    expected_impact = table.Column<string>(type: "text", nullable: true),
                    evidence = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analysis_recommendations", x => x.id);
                    table.ForeignKey(
                        name: "fk_analysis_recommendations_analysis_jobs_analysis_job_id",
                        column: x => x.analysis_job_id,
                        principalTable: "analysis_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analysis_recommendations_job_priority",
                table: "analysis_recommendations",
                columns: new[] { "analysis_job_id", "priority", "sort_order" });

            // GRANT SELECT для analytics_reader на новую таблицу (Web API Analytics-модуль, ADR-011).
            // DO-блок защищает от падения, если роли ещё нет (Testcontainers БД её не создаёт).
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'analytics_reader') THEN
                        GRANT SELECT ON analysis_recommendations TO analytics_reader;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema_2_0 — one-way migration. Откат через drop volume + повторный apply
            // только Initial-миграции. Не пишем обратные DROP/ADD, потому что данные
            // в schema 2.0 невозможно семантически отобразить обратно в 1.0
            // (aspects-объекты не сводятся к плоскому списку topics + sentiment-скаляру).
            throw new NotSupportedException(
                "Schema_2_0 is one-way. Recreate volume to revert to schema 1.0.");
        }
    }
}
