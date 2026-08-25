using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VMentory.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEstate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Actions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Ref = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Why = table.Column<string>(type: "TEXT", nullable: false),
                    How = table.Column<string>(type: "TEXT", nullable: false),
                    DoneWhen = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    Class = table.Column<string>(type: "TEXT", nullable: false),
                    Group = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    LastDetectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AffectedMachinesJson = table.Column<string>(type: "TEXT", nullable: false),
                    AffectedVmsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PurchasesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RequiresDowntime = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduledStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ScheduledEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Actions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HardwareSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TakenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    ServiceTag = table.Column<string>(type: "TEXT", nullable: true),
                    ManagementIp = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    StaticVmsJson = table.Column<string>(type: "TEXT", nullable: false),
                    StaticVmsVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StaticVmsSource = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ActionDependencies",
                columns: table => new
                {
                    BlockedId = table.Column<string>(type: "TEXT", nullable: false),
                    BlockerId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionDependencies", x => new { x.BlockedId, x.BlockerId });
                    table.ForeignKey(
                        name: "FK_ActionDependencies_Actions_BlockedId",
                        column: x => x.BlockedId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionDependencies_Actions_BlockerId",
                        column: x => x.BlockerId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActionId = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    By = table.Column<string>(type: "TEXT", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionNotes_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionDependencies_BlockerId",
                table: "ActionDependencies",
                column: "BlockerId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionNotes_ActionId",
                table: "ActionNotes",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_Ref",
                table: "Actions",
                column: "Ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Actions_SourceKey",
                table: "Actions",
                column: "SourceKey");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_Status",
                table: "Actions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareSnapshots_TakenAt",
                table: "HardwareSnapshots",
                column: "TakenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Machines_Address",
                table: "Machines",
                column: "Address");

            migrationBuilder.CreateIndex(
                name: "IX_Machines_ServiceTag",
                table: "Machines",
                column: "ServiceTag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionDependencies");

            migrationBuilder.DropTable(
                name: "ActionNotes");

            migrationBuilder.DropTable(
                name: "HardwareSnapshots");

            migrationBuilder.DropTable(
                name: "Machines");

            migrationBuilder.DropTable(
                name: "Actions");
        }
    }
}
