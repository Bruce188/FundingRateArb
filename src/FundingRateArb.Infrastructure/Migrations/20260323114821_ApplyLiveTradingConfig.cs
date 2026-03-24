using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ApplyLiveTradingConfig : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Only update rows that still have previous defaults, preserving user-customized values.
        migrationBuilder.Sql(@"
                UPDATE BotConfigurations SET
                    TotalCapitalUsdc = 39,
                    MinPositionSizeUsdc = 5,
                    MaxCapitalPerPosition = 0.90,
                    OpenThreshold = 0.0002,
                    BreakevenHoursMax = 8,
                    AdaptiveHoldEnabled = 1,
                    StopLossPct = 0.10,
                    DailyDrawdownPausePct = 0.08,
                    MaxHoldTimeHours = 48,
                    FeeAmortizationHours = 12
                WHERE TotalCapitalUsdc = 107
                  AND OpenThreshold = 0.0003
            ");

        migrationBuilder.Sql(@"
                UPDATE UserConfigurations SET
                    TotalCapitalUsdc = 39,
                    MaxConcurrentPositions = 1,
                    OpenThreshold = 0.0002,
                    StopLossPct = 0.10,
                    MaxCapitalPerPosition = 0.90,
                    MaxHoldTimeHours = 48,
                    FeeAmortizationHours = 12,
                    DailyDrawdownPausePct = 0.08
                WHERE TotalCapitalUsdc = 100
                  AND OpenThreshold = 0.0001
            ");

        migrationBuilder.Sql(@"
                UPDATE Exchanges SET TakerFeeRate = 0.00045 WHERE Name = 'Hyperliquid';
                UPDATE Exchanges SET TakerFeeRate = 0 WHERE Name = 'Lighter';
                UPDATE Exchanges SET TakerFeeRate = 0.0004 WHERE Name = 'Aster';
            ");

        // Align DB column default with C# entity default (AdaptiveHoldEnabled = true)
        migrationBuilder.AlterColumn<bool>(
            name: "AdaptiveHoldEnabled",
            table: "BotConfigurations",
            nullable: false,
            defaultValue: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
                UPDATE BotConfigurations SET
                    TotalCapitalUsdc = 107,
                    MinPositionSizeUsdc = 10,
                    MaxCapitalPerPosition = 0.80,
                    OpenThreshold = 0.0003,
                    BreakevenHoursMax = 6,
                    AdaptiveHoldEnabled = 0,
                    StopLossPct = 0.15,
                    DailyDrawdownPausePct = 0.05,
                    MaxHoldTimeHours = 72,
                    FeeAmortizationHours = 24
                WHERE TotalCapitalUsdc = 39
                  AND OpenThreshold = 0.0002
            ");

        migrationBuilder.Sql(@"
                UPDATE UserConfigurations SET
                    TotalCapitalUsdc = 100,
                    MaxConcurrentPositions = 3,
                    OpenThreshold = 0.0001,
                    StopLossPct = 0.05,
                    MaxCapitalPerPosition = 0.5,
                    MaxHoldTimeHours = 72,
                    FeeAmortizationHours = 24,
                    DailyDrawdownPausePct = 0.1
                WHERE TotalCapitalUsdc = 39
                  AND OpenThreshold = 0.0002
            ");

        migrationBuilder.Sql(@"
                UPDATE Exchanges SET TakerFeeRate = NULL WHERE Name IN ('Hyperliquid', 'Lighter', 'Aster');
            ");
    }
}
