using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obratka.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyBranchIsSelected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSelected",
                table: "company_branches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Branches that already existed before this column got introduced were inserted
            // via SaveBranches → semantically "selected". Backfill so admins don't see them as candidates.
            migrationBuilder.Sql(@"UPDATE company_branches SET ""IsSelected"" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSelected",
                table: "company_branches");
        }
    }
}
