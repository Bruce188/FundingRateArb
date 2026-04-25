using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddDivergenceMonitoringFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DivergenceAlertConfirmationCycles",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<bool>(
            name: "PreferCloseOnDivergenceNarrowing",
            table: "BotConfigurations",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<decimal>(
            name: "RotationDivergenceHorizonHours",
            table: "BotConfigurations",
            type: "decimal(5,2)",
            nullable: false,
            defaultValue: 2.0m);

        migrationBuilder.AddColumn<decimal>(
            name: "PrevDivergencePct",
            table: "ArbitragePositions",
            type: "decimal(8,4)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DivergenceAlertConfirmationCycles",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "PreferCloseOnDivergenceNarrowing",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "RotationDivergenceHorizonHours",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "PrevDivergencePct",
            table: "ArbitragePositions");
    }
}
