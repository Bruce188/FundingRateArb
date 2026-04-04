using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskMonitoringProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FundingNotionalPriceType",
                table: "Exchanges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "FundingRebateRate",
                table: "Exchanges",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "FundingTimingDeviationSeconds",
                table: "Exchanges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FundingFlipExitCycles",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinConsecutiveFavorableCycles",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "StablecoinAlertThresholdPct",
                table: "BotConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0.3m);

            migrationBuilder.AddColumn<decimal>(
                name: "StablecoinCriticalThresholdPct",
                table: "BotConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 1.0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FundingNotionalPriceType",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "FundingRebateRate",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "FundingTimingDeviationSeconds",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "FundingFlipExitCycles",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "MinConsecutiveFavorableCycles",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "StablecoinAlertThresholdPct",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "StablecoinCriticalThresholdPct",
                table: "BotConfigurations");
        }
    }
}
