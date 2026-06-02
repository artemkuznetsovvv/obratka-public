using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obratka.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monitoring_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeedJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sources = table.Column<List<string>>(type: "text[]", nullable: false),
                    BranchIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    WindowDays = table.Column<int>(type: "integer", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CronSchedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastCollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_monitoring_configs_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_cycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitoringId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleNumber = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeriodFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PeriodTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NewReviewCount = table.Column<int>(type: "integer", nullable: false),
                    TotalReviewsAtCycle = table.Column<int>(type: "integer", nullable: false),
                    NegativeRatioPp = table.Column<double>(type: "double precision", nullable: false),
                    NegativeSpikeTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    SummarySnapshot = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RecommendationsSnapshot = table.Column<string>(type: "jsonb", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_cycles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_monitoring_cycles_monitoring_configs_MonitoringId",
                        column: x => x.MonitoringId,
                        principalTable: "monitoring_configs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_configs_CompanyId",
                table: "monitoring_configs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_configs_SeedJobId",
                table: "monitoring_configs",
                column: "SeedJobId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_configs_UserId",
                table: "monitoring_configs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_cycles_MonitoringId",
                table: "monitoring_cycles",
                column: "MonitoringId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_cycles_MonitoringId_CycleNumber",
                table: "monitoring_cycles",
                columns: new[] { "MonitoringId", "CycleNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitoring_cycles");

            migrationBuilder.DropTable(
                name: "monitoring_configs");
        }
    }
}
