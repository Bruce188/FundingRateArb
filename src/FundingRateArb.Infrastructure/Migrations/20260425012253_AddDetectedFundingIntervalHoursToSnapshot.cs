using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDetectedFundingIntervalHoursToSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArbitragePositions_Status_OpenConfirmedAt",
                table: "ArbitragePositions");

            migrationBuilder.AddColumn<int>(
                name: "DetectedFundingIntervalHours",
                table: "FundingRateSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "OpenConfirmTimeoutSeconds",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectedFundingIntervalHours",
                table: "FundingRateSnapshots");

            migrationBuilder.AlterColumn<int>(
                name: "OpenConfirmTimeoutSeconds",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateIndex(
                name: "IX_ArbitragePositions_Status_OpenConfirmedAt",
                table: "ArbitragePositions",
                columns: new[] { "Status", "OpenConfirmedAt" });
        }
    }
}
