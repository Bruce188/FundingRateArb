using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class BotConfigRework : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ConsecutiveLossPause",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 3);

        migrationBuilder.AddColumn<decimal>(
            name: "DailyDrawdownPausePct",
            table: "BotConfigurations",
            type: "decimal(18,4)",
            nullable: false,
            defaultValue: 0.05m);

        migrationBuilder.AddColumn<int>(
            name: "FeeAmortizationHours",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 24);

        migrationBuilder.AddColumn<decimal>(
            name: "MinPositionSizeUsdc",
            table: "BotConfigurations",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 10m);

        migrationBuilder.AddColumn<decimal>(
            name: "MinVolume24hUsdc",
            table: "BotConfigurations",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 50000m);

        migrationBuilder.AddColumn<int>(
            name: "RateStalenessMinutes",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 15);

        // Backfill existing rows with correct defaults
        migrationBuilder.Sql(@"
                UPDATE BotConfigurations SET
                    FeeAmortizationHours = 24,
                    MinPositionSizeUsdc = 10,
                    MinVolume24hUsdc = 50000,
                    RateStalenessMinutes = 15,
                    DailyDrawdownPausePct = 0.05,
                    ConsecutiveLossPause = 3
                WHERE FeeAmortizationHours = 0
            ");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ConsecutiveLossPause",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "DailyDrawdownPausePct",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "FeeAmortizationHours",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "MinPositionSizeUsdc",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "MinVolume24hUsdc",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "RateStalenessMinutes",
            table: "BotConfigurations");
    }
}
