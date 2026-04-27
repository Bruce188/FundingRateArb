using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSlippageAttributionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxAcceptableSlippagePct",
                table: "BotConfigurations",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0.001m);

            migrationBuilder.AddColumn<decimal>(
                name: "LongEntrySlippagePct",
                table: "ArbitragePositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LongExitSlippagePct",
                table: "ArbitragePositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LongIntendedMidAtSubmit",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortEntrySlippagePct",
                table: "ArbitragePositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortExitSlippagePct",
                table: "ArbitragePositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortIntendedMidAtSubmit",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxAcceptableSlippagePct",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "LongEntrySlippagePct",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "LongExitSlippagePct",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "LongIntendedMidAtSubmit",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortEntrySlippagePct",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortExitSlippagePct",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortIntendedMidAtSubmit",
                table: "ArbitragePositions");
        }
    }
}
