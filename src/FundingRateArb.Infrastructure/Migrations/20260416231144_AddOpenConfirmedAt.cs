using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddOpenConfirmedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "LighterSlippageFloorPct",
            table: "BotConfigurations",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<decimal>(
            name: "LighterSlippageMaxPct",
            table: "BotConfigurations",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<int>(
            name: "OpenConfirmTimeoutSeconds",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 30);

        migrationBuilder.AddColumn<decimal>(
            name: "PnlTargetUnifiedTolerance",
            table: "BotConfigurations",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<DateTime>(
            name: "OpenConfirmedAt",
            table: "ArbitragePositions",
            type: "datetime2",
            nullable: true);

        // Composite index to support the boot-time pending-confirm sweep without a full scan.
        // Filters on Status=Opening (int 0) and OpenConfirmedAt IS NULL for efficient lookup.
        // Note: this migration also absorbs prior unmigrated model-snapshot drift for
        // LighterSlippageFloorPct, LighterSlippageMaxPct, and PnlTargetUnifiedTolerance.
        migrationBuilder.CreateIndex(
            name: "IX_ArbitragePositions_Status_OpenConfirmedAt",
            table: "ArbitragePositions",
            columns: ["Status", "OpenConfirmedAt"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ArbitragePositions_Status_OpenConfirmedAt",
            table: "ArbitragePositions");

        migrationBuilder.DropColumn(
            name: "LighterSlippageFloorPct",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "LighterSlippageMaxPct",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "OpenConfirmTimeoutSeconds",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "PnlTargetUnifiedTolerance",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "OpenConfirmedAt",
            table: "ArbitragePositions");
    }
}
