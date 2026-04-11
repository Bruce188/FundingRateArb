using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddLiquidationEarlyWarningPct : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "LiquidationEarlyWarningPct",
            table: "BotConfigurations",
            type: "decimal(18,4)",
            nullable: false,
            defaultValue: 0.75m);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LiquidationEarlyWarningPct",
            table: "BotConfigurations");
    }
}
