using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPnlReconciliationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeReportedFees",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeReportedFunding",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeReportedPnl",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PnlDivergence",
                table: "ArbitragePositions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReconciledAt",
                table: "ArbitragePositions",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeReportedFees",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ExchangeReportedFunding",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ExchangeReportedPnl",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "PnlDivergence",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ReconciledAt",
                table: "ArbitragePositions");
        }
    }
}
