using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeeTrackingAndClosingStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosingStartedAt",
                table: "ArbitragePositions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EntryFeesUsdc",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExitFeesUsdc",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosingStartedAt",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "EntryFeesUsdc",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ExitFeesUsdc",
                table: "ArbitragePositions");
        }
    }
}
