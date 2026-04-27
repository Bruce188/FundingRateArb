using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class AssetExchangeFundingIntervalConfiguration : IEntityTypeConfiguration<AssetExchangeFundingInterval>
{
    public void Configure(EntityTypeBuilder<AssetExchangeFundingInterval> builder)
    {
        builder.HasKey(f => new { f.ExchangeId, f.AssetId });

        builder.HasOne(f => f.Exchange)
            .WithMany()
            .HasForeignKey(f => f.ExchangeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.Asset)
            .WithMany()
            .HasForeignKey(f => f.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // SourceSnapshotId is a soft reference (no FK constraint) — preserves
        // observability link without creating multi-cascade-path conflict with
        // Asset/Exchange cascades on FundingRateSnapshots.
        builder.HasIndex(f => f.SourceSnapshotId);
    }
}
