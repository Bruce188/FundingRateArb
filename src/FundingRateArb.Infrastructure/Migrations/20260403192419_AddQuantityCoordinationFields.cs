using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddQuantityCoordinationFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ForceConcurrentExecution",
            table: "BotConfigurations",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<decimal>(
            name: "LongFilledQuantity",
            table: "ArbitragePositions",
            type: "decimal(28,12)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "ShortFilledQuantity",
            table: "ArbitragePositions",
            type: "decimal(28,12)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ForceConcurrentExecution",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "LongFilledQuantity",
            table: "ArbitragePositions");

        migrationBuilder.DropColumn(
            name: "ShortFilledQuantity",
            table: "ArbitragePositions");
    }
}
