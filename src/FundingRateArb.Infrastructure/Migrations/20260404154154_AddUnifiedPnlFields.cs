using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddUnifiedPnlFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "DivergenceAlertMultiplier",
            table: "BotConfigurations",
            type: "decimal(18,4)",
            nullable: false,
            defaultValue: 2.0m);

        migrationBuilder.AddColumn<decimal>(
            name: "CurrentDivergencePct",
            table: "ArbitragePositions",
            type: "decimal(18,4)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DivergenceAlertMultiplier",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "CurrentDivergencePct",
            table: "ArbitragePositions");
    }
}
