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
            migrationBuilder.DropIndex(
                name: "IX_Alerts_ArbitragePositionId",
                table: "Alerts");

            migrationBuilder.AddColumn<int>(
                name: "FundingFlipExitCycles",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "MinConsecutiveFavorableCycles",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 3);

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

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_PositionId_Type_CreatedAt",
                table: "Alerts",
                columns: new[] { "ArbitragePositionId", "Type", "CreatedAt" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Alerts_PositionId_Type_CreatedAt",
                table: "Alerts");

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

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ArbitragePositionId",
                table: "Alerts",
                column: "ArbitragePositionId");
        }
    }
}
