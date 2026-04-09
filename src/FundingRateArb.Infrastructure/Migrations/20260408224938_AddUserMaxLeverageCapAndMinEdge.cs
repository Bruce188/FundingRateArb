using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddUserMaxLeverageCapAndMinEdge : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MaxLeverageCap",
            table: "UserConfigurations",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "MinEdgeMultiplier",
            table: "BotConfigurations",
            type: "decimal(18,4)",
            nullable: false,
            defaultValue: 3m);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MaxLeverageCap",
            table: "UserConfigurations");

        migrationBuilder.DropColumn(
            name: "MinEdgeMultiplier",
            table: "BotConfigurations");
    }
}
