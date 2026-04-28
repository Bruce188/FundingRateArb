using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddReconciliationReports : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ReconciliationIntervalMinutes",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 5);

        migrationBuilder.CreateTable(
            name: "ReconciliationReports",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                RunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                DurationMs = table.Column<int>(type: "int", nullable: false),
                OverallStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PerExchangeEquityJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                FreshRateMismatchCount = table.Column<int>(type: "int", nullable: false),
                OrphanPositionCount = table.Column<int>(type: "int", nullable: false),
                PhantomFeeRowCount24h = table.Column<int>(type: "int", nullable: false),
                FeeDeltaOutsideToleranceCount = table.Column<int>(type: "int", nullable: false),
                DegradedExchangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                AnomalySummary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReconciliationReports", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReconciliationReports_RunAtUtc",
            table: "ReconciliationReports",
            column: "RunAtUtc",
            descending: Array.Empty<bool>());
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ReconciliationReports");

        migrationBuilder.DropColumn(
            name: "ReconciliationIntervalMinutes",
            table: "BotConfigurations");
    }
}
