using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obratka.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLogicalBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "company_branches",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalBranchId",
                table: "company_branches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "company_branches",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "logical_branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Address = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logical_branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_logical_branches_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_branches_LogicalBranchId",
                table: "company_branches",
                column: "LogicalBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_logical_branches_CompanyId",
                table: "logical_branches",
                column: "CompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_company_branches_logical_branches_LogicalBranchId",
                table: "company_branches",
                column: "LogicalBranchId",
                principalTable: "logical_branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_company_branches_logical_branches_LogicalBranchId",
                table: "company_branches");

            migrationBuilder.DropTable(
                name: "logical_branches");

            migrationBuilder.DropIndex(
                name: "IX_company_branches_LogicalBranchId",
                table: "company_branches");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "company_branches");

            migrationBuilder.DropColumn(
                name: "LogicalBranchId",
                table: "company_branches");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "company_branches");
        }
    }
}
