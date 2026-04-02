using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class FundingRateSnapshotConfiguration : IEntityTypeConfiguration<FundingRateSnapshot>
{
    public void Configure(EntityTypeBuilder<FundingRateSnapshot> builder)
    {
        builder.HasKey(f => f.Id);

        builder.Property(f => f.RatePerHour).HasColumnType("decimal(18,10)");
        builder.Property(f => f.RawRate).HasColumnType("decimal(18,10)");
        builder.Property(f => f.MarkPrice).HasColumnType("decimal(18,4)");
        builder.Property(f => f.IndexPrice).HasColumnType("decimal(18,4)");
        builder.Property(f => f.Volume24hUsd).HasColumnType("decimal(18,2)");

        builder.HasOne(f => f.Exchange)
            .WithMany(e => e.FundingRateSnapshots)
            .HasForeignKey(f => f.ExchangeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.Asset)
            .WithMany(a => a.FundingRateSnapshots)
            .HasForeignKey(f => f.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => new { f.ExchangeId, f.AssetId, f.RecordedAt })
            .HasDatabaseName("IX_FundingRateSnapshots_Exchange_Asset_RecordedAt")
            .IsDescending(false, false, true);
    }
}
