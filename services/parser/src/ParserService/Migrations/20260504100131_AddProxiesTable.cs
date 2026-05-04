using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParserService.Migrations
{
    /// <inheritdoc />
    public partial class AddProxiesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Proxies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    Password = table.Column<string>(type: "TEXT", nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proxies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_Enabled",
                table: "Proxies",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_Host_Port_Username",
                table: "Proxies",
                columns: new[] { "Host", "Port", "Username" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Proxies");
        }
    }
}
