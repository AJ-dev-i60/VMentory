using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VMentory.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DrainPlans",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SourceHostId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceNode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ReverseOfPlanId = table.Column<string>(type: "TEXT", nullable: true),
                    ActionId = table.Column<string>(type: "TEXT", nullable: true),
                    MaintenanceUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MaintenanceReason = table.Column<string>(type: "TEXT", nullable: true),
                    AbortRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrainPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FleetRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MembersJson = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DrainPlanId = table.Column<string>(type: "TEXT", nullable: true),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    RowAction = table.Column<string>(type: "TEXT", nullable: false),
                    PlanNote = table.Column<string>(type: "TEXT", nullable: true),
                    GuestType = table.Column<string>(type: "TEXT", nullable: false),
                    Vmid = table.Column<int>(type: "INTEGER", nullable: false),
                    GuestName = table.Column<string>(type: "TEXT", nullable: false),
                    WasRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceHostId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceNode = table.Column<string>(type: "TEXT", nullable: false),
                    TargetHostId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetNode = table.Column<string>(type: "TEXT", nullable: true),
                    TargetVmid = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetStorage = table.Column<string>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    BytesPlanned = table.Column<long>(type: "INTEGER", nullable: true),
                    BytesDone = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: true),
                    Upid = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    LogTail = table.Column<string>(type: "TEXT", nullable: true),
                    PreflightJson = table.Column<string>(type: "TEXT", nullable: true),
                    TargetVmidWasFree = table.Column<bool>(type: "INTEGER", nullable: false),
                    PartialOnTarget = table.Column<bool>(type: "INTEGER", nullable: false),
                    CleanupDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceRestarted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    QueuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TransferStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TransferEndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeMaintenance",
                columns: table => new
                {
                    HostId = table.Column<string>(type: "TEXT", nullable: false),
                    Since = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Until = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    By = table.Column<string>(type: "TEXT", nullable: true),
                    DrainPlanId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeMaintenance", x => x.HostId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_DrainPlanId",
                table: "MigrationJobs",
                column: "DrainPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobs_Status",
                table: "MigrationJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrainPlans");

            migrationBuilder.DropTable(
                name: "FleetRules");

            migrationBuilder.DropTable(
                name: "MigrationJobs");

            migrationBuilder.DropTable(
                name: "NodeMaintenance");
        }
    }
}
