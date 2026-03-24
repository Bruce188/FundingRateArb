using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddTakerFeeRateAndIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "TakerFeeRate",
            table: "Exchanges",
            type: "decimal(18,8)",
            precision: 18,
            scale: 8,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ArbitragePositions_Status",
            table: "ArbitragePositions",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_Alerts_IsRead_CreatedAt",
            table: "Alerts",
            columns: new[] { "IsRead", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ArbitragePositions_Status",
            table: "ArbitragePositions");

        migrationBuilder.DropIndex(
            name: "IX_Alerts_IsRead_CreatedAt",
            table: "Alerts");

        migrationBuilder.DropColumn(
            name: "TakerFeeRate",
            table: "Exchanges");
    }
}
