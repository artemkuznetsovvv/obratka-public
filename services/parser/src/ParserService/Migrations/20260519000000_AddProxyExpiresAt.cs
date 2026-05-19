using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParserService.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyExpiresAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "Proxies",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Proxies");
        }
    }
}
