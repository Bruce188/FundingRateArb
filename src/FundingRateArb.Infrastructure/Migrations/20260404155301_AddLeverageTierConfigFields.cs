using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddLeverageTierConfigFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "MarginUtilizationAlertPct",
            table: "BotConfigurations",
            type: "decimal(18,4)",
            nullable: false,
            defaultValue: 0.70m);

        migrationBuilder.AddColumn<int>(
            name: "MaxLeverageCap",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 3);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MarginUtilizationAlertPct",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "MaxLeverageCap",
            table: "BotConfigurations");
    }
}
