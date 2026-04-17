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
            defaultValue: 0);

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
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
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
