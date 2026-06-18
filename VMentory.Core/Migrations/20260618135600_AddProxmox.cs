using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VMentory.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipTlsVerification",
                table: "Hosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipTlsVerification",
                table: "Hosts");
        }
    }
}
