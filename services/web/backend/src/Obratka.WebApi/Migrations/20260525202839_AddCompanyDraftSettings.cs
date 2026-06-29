using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obratka.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyDraftSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DraftPeriodFrom",
                table: "companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DraftPeriodTo",
                table: "companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "DraftSources",
                table: "companies",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftPeriodFrom",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "DraftPeriodTo",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "DraftSources",
                table: "companies");
        }
    }
}
