using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCoinGlassAnalyticsEntities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsPlanned",
            table: "Exchanges",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "CoinGlassDiscoveryEvents",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                EventType = table.Column<int>(type: "int", nullable: false),
                ExchangeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                DiscoveredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CoinGlassDiscoveryEvents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CoinGlassExchangeRates",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                SnapshotTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                SourceExchange = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                RawRate = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                RatePerHour = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                IntervalHours = table.Column<int>(type: "int", nullable: false),
                MarkPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                IndexPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                Volume24hUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CoinGlassExchangeRates", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CoinGlassDiscoveryEvents_DiscoveredAt",
            table: "CoinGlassDiscoveryEvents",
            column: "DiscoveredAt");

        migrationBuilder.CreateIndex(
            name: "IX_CoinGlassExchangeRates_SnapshotTime",
            table: "CoinGlassExchangeRates",
            column: "SnapshotTime");

        migrationBuilder.CreateIndex(
            name: "IX_CoinGlassExchangeRates_SourceExchange_Symbol_SnapshotTime",
            table: "CoinGlassExchangeRates",
            columns: new[] { "SourceExchange", "Symbol", "SnapshotTime" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CoinGlassDiscoveryEvents");

        migrationBuilder.DropTable(
            name: "CoinGlassExchangeRates");

        migrationBuilder.DropColumn(
            name: "IsPlanned",
            table: "Exchanges");
    }
}
