using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VMentory.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddForceStop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForceStopOnTimeout",
                table: "MigrationJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForceStopOnTimeout",
                table: "MigrationJobs");
        }
    }
}
