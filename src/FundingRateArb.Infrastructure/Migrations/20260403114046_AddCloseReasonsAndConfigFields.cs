using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCloseReasonsAndConfigFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DryRunEnabled",
                table: "UserConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxRotationsPerDay",
                table: "UserConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinHoldBeforeRotationMinutes",
                table: "UserConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "RotationThresholdPerHour",
                table: "UserConfigurations",
                type: "decimal(18,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "DryRunEnabled",
                table: "BotConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReconciliationIntervalCycles",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsDryRun",
                table: "ArbitragePositions",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DryRunEnabled",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "MaxRotationsPerDay",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "MinHoldBeforeRotationMinutes",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "RotationThresholdPerHour",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "DryRunEnabled",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "ReconciliationIntervalCycles",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "IsDryRun",
                table: "ArbitragePositions");
        }
    }
}
