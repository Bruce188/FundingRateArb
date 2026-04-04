using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class BotConfigurationConfiguration : IEntityTypeConfiguration<BotConfiguration>
{
    public void Configure(EntityTypeBuilder<BotConfiguration> builder)
    {
        builder.HasKey(b => b.Id);

        builder.Property(b => b.OpenThreshold).HasColumnType("decimal(18,10)");
        builder.Property(b => b.AlertThreshold).HasColumnType("decimal(18,10)");
        builder.Property(b => b.CloseThreshold).HasColumnType("decimal(18,10)");
        builder.Property(b => b.StopLossPct).HasColumnType("decimal(18,4)");
        builder.Property(b => b.VolumeFraction).HasColumnType("decimal(18,6)");
        builder.Property(b => b.MaxCapitalPerPosition).HasColumnType("decimal(18,4)");
        builder.Property(b => b.TotalCapitalUsdc).HasColumnType("decimal(18,2)");
        builder.Property(b => b.MinPositionSizeUsdc).HasColumnType("decimal(18,2)");
        builder.Property(b => b.MinVolume24hUsdc).HasColumnType("decimal(18,2)");
        builder.Property(b => b.DailyDrawdownPausePct).HasColumnType("decimal(18,4)");
        builder.Property(b => b.MaxExposurePerAsset).HasColumnType("decimal(18,4)");
        builder.Property(b => b.MaxExposurePerExchange).HasColumnType("decimal(18,4)");
        builder.Property(b => b.TargetPnlMultiplier).HasColumnType("decimal(18,4)").HasDefaultValue(2.0m);
        builder.Property(b => b.AdaptiveHoldEnabled).HasDefaultValue(true);
        builder.Property(b => b.RebalanceEnabled).HasDefaultValue(false);
        builder.Property(b => b.RebalanceMinImprovement).HasColumnType("decimal(18,6)").HasDefaultValue(0.0002m);
        builder.Property(b => b.MaxRebalancesPerCycle).HasDefaultValue(2);
        builder.Property(b => b.EmergencyCloseSpreadThreshold).HasColumnType("decimal(18,6)").HasDefaultValue(-0.001m);
        builder.Property(b => b.LiquidationWarningPct).HasColumnType("decimal(18,4)").HasDefaultValue(0.50m);
        builder.Property(b => b.ReconciliationIntervalCycles).HasDefaultValue(10);
        builder.Property(b => b.DivergenceAlertMultiplier).HasColumnType("decimal(18,4)").HasDefaultValue(2.0m);
        builder.Property(b => b.DryRunEnabled).HasDefaultValue(false);
    }
}
