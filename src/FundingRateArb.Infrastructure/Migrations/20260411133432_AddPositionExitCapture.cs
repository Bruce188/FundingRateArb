using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionExitCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LongExitPrice",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LongExitQty",
                table: "ArbitragePositions",
                type: "decimal(28,12)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortExitPrice",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortExitQty",
                table: "ArbitragePositions",
                type: "decimal(28,12)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LongExitPrice",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "LongExitQty",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortExitPrice",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortExitQty",
                table: "ArbitragePositions");
        }
    }
}
