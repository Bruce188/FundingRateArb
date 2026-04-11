using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class ArbitragePositionConfiguration : IEntityTypeConfiguration<ArbitragePosition>
{
    public void Configure(EntityTypeBuilder<ArbitragePosition> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.SizeUsdc).HasColumnType("decimal(18,2)");
        builder.Property(p => p.MarginUsdc).HasColumnType("decimal(18,2)");
        builder.Property(p => p.LongEntryPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ShortEntryPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.EntrySpreadPerHour).HasColumnType("decimal(18,10)");
        builder.Property(p => p.CurrentSpreadPerHour).HasColumnType("decimal(18,10)");
        builder.Property(p => p.AccumulatedFunding).HasColumnType("decimal(18,4)");
        builder.Property(p => p.EntryFeesUsdc).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ExitFeesUsdc).HasColumnType("decimal(18,4)");
        builder.Property(p => p.LongLiquidationPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ShortLiquidationPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RealizedPnl).HasColumnType("decimal(18,4)");
        builder.Property(p => p.LongFilledQuantity).HasColumnType("decimal(28,12)");
        builder.Property(p => p.ShortFilledQuantity).HasColumnType("decimal(28,12)");
        builder.Property(p => p.LongExitPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ShortExitPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.LongExitQty).HasColumnType("decimal(28,12)");
        builder.Property(p => p.ShortExitQty).HasColumnType("decimal(28,12)");
        builder.Property(p => p.ExchangeReportedPnl).HasColumnType("decimal(18,4)");
        builder.Property(p => p.PnlDivergence).HasColumnType("decimal(18,4)");
        builder.Property(p => p.CurrentDivergencePct).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ExchangeReportedFunding).HasColumnType("decimal(18,4)");
        builder.Property(p => p.LongLegClosed).HasDefaultValue(false);
        builder.Property(p => p.ShortLegClosed).HasDefaultValue(false);
        builder.Property(p => p.LongOrderId).HasMaxLength(200);
        builder.Property(p => p.ShortOrderId).HasMaxLength(200);
        builder.Property(p => p.Notes).HasMaxLength(500);

        // Two FKs to Exchange — must use Restrict to avoid multiple cascade paths
        builder.HasOne(p => p.LongExchange)
            .WithMany(e => e.LongPositions)
            .HasForeignKey(p => p.LongExchangeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.ShortExchange)
            .WithMany(e => e.ShortPositions)
            .HasForeignKey(p => p.ShortExchangeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Asset)
            .WithMany(a => a.Positions)
            .HasForeignKey(p => p.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.User)
            .WithMany(u => u.Positions)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.Status);
    }
}
