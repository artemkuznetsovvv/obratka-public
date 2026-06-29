using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obratka.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class CompanyBranchOptionalExternalRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_company_branches_CompanyId_Source_ExternalId",
                table: "company_branches");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUrl",
                table: "company_branches",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            migrationBuilder.CreateIndex(
                name: "IX_company_branches_CompanyId_Source_ExternalId",
                table: "company_branches",
                columns: new[] { "CompanyId", "Source", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_company_branches_CompanyId_Source_ExternalId",
                table: "company_branches");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUrl",
                table: "company_branches",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_company_branches_CompanyId_Source_ExternalId",
                table: "company_branches",
                columns: new[] { "CompanyId", "Source", "ExternalId" },
                unique: true);
        }
    }
}
