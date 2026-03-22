using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExposureLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxExposurePerAsset",
                table: "UserConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0.5m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxExposurePerExchange",
                table: "UserConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0.7m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxExposurePerAsset",
                table: "BotConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0.5m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxExposurePerExchange",
                table: "BotConfigurations",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0.7m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxExposurePerAsset",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "MaxExposurePerExchange",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "MaxExposurePerAsset",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "MaxExposurePerExchange",
                table: "BotConfigurations");
        }
    }
}
