using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedBinanceAndDydxExchanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insert Binance exchange if it doesn't already exist.
            // Values match DbSeeder.cs lines 115-130.
            // FundingInterval: EightHourly = 2, FundingSettlementType: Periodic = 1
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM Exchanges WHERE Name = 'Binance')
                BEGIN
                    INSERT INTO Exchanges
                        (Name, ApiBaseUrl, WsBaseUrl, FundingInterval, FundingIntervalHours,
                         FundingSettlementType, FundingNotionalPriceType, SupportsSubAccounts,
                         IsActive, IsDataOnly, Description, TakerFeeRate)
                    VALUES
                        ('Binance', 'https://fapi.binance.com', 'wss://fstream.binance.com',
                         2, 8, 1, 0, 0, 1, 0,
                         'Binance Futures — HMAC-SHA256, USDT collateral, 8-hour funding', 0.0005)
                END
            ");

            // Insert dYdX exchange if it doesn't already exist.
            // Values match DbSeeder.cs lines 131-141.
            // FundingInterval: Hourly = 1, FundingSettlementType: Continuous = 0, FundingNotionalPriceType: OraclePrice = 1
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM Exchanges WHERE Name = 'dYdX')
                BEGIN
                    INSERT INTO Exchanges
                        (Name, ApiBaseUrl, WsBaseUrl, FundingInterval, FundingIntervalHours,
                         FundingSettlementType, FundingNotionalPriceType, SupportsSubAccounts,
                         IsActive, IsDataOnly, Description, TakerFeeRate)
                    VALUES
                        ('dYdX', 'https://indexer.dydx.trade/v4', '',
                         1, 1, 0, 1, 1, 1, 0,
                         'dYdX v4 — Cosmos appchain, USDC collateral, hourly funding', 0.0003)
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is intentionally a no-op.
            // Removing exchange rows would orphan any UserExchangeCredential rows that reference them.
            // If you need to reverse this migration, delete credential rows first, then remove the exchanges manually.
        }
    }
}
