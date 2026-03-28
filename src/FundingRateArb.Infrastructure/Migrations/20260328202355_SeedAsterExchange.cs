using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SeedAsterExchange : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Insert Aster exchange if it doesn't already exist.
        // Values match DbSeeder.cs + ApplyLiveTradingConfig migration (TakerFeeRate).
        // FundingInterval: EightHourly = 2, FundingSettlementType: Periodic = 1
        migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM Exchanges WHERE Name = 'Aster')
                BEGIN
                    INSERT INTO Exchanges
                        (Name, ApiBaseUrl, WsBaseUrl, FundingInterval, FundingIntervalHours,
                         FundingSettlementType, SupportsSubAccounts, IsActive, IsDataOnly,
                         Description, TakerFeeRate)
                    VALUES
                        ('Aster', 'https://fapi.asterdex.com', 'wss://fstream.asterdex.com',
                         2, 8, 1, 0, 1, 0,
                         'Aster DEX — HMAC-SHA256, USDT collateral, 8-hour funding', 0.0004)
                END
            ");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"DELETE FROM Exchanges WHERE Name = 'Aster';");
    }
}
