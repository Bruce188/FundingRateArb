using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoinGlassAnalyticsUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoinGlassExchangeRates_SourceExchange_Symbol_SnapshotTime",
                table: "CoinGlassExchangeRates");

            migrationBuilder.CreateIndex(
                name: "IX_CoinGlassExchangeRates_SourceExchange_Symbol_SnapshotTime",
                table: "CoinGlassExchangeRates",
                columns: new[] { "SourceExchange", "Symbol", "SnapshotTime" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoinGlassExchangeRates_SourceExchange_Symbol_SnapshotTime",
                table: "CoinGlassExchangeRates");

            migrationBuilder.CreateIndex(
                name: "IX_CoinGlassExchangeRates_SourceExchange_Symbol_SnapshotTime",
                table: "CoinGlassExchangeRates",
                columns: new[] { "SourceExchange", "Symbol", "SnapshotTime" });
        }
    }
}
