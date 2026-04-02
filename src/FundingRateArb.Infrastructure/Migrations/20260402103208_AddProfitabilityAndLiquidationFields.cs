using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfitabilityAndLiquidationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerExchange",
                table: "UserConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerAsset",
                table: "UserConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerExchange",
                table: "BotConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerAsset",
                table: "BotConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AddColumn<decimal>(
                name: "EmergencyCloseSpreadThreshold",
                table: "BotConfigurations",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: -0.001m);

            migrationBuilder.AddColumn<decimal>(
                name: "LiquidationWarningPct",
                table: "BotConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0.50m);

            migrationBuilder.AddColumn<int>(
                name: "MinHoldBeforePnlTargetMinutes",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "PriceFeedFailureCloseThreshold",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "SlippageBufferBps",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<decimal>(
                name: "LongLiquidationPrice",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortLiquidationPrice",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmergencyCloseSpreadThreshold",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "LiquidationWarningPct",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "MinHoldBeforePnlTargetMinutes",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "PriceFeedFailureCloseThreshold",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "SlippageBufferBps",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "LongLiquidationPrice",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortLiquidationPrice",
                table: "ArbitragePositions");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerExchange",
                table: "UserConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerAsset",
                table: "UserConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerExchange",
                table: "BotConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxExposurePerAsset",
                table: "BotConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)");
        }
    }
}
