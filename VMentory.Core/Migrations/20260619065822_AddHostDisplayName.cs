using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VMentory.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHostDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Hosts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Hosts");
        }
    }
}
