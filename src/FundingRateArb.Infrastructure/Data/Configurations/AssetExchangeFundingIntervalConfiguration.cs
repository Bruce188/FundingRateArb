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

        builder.HasOne<FundingRateSnapshot>()
            .WithMany()
            .HasForeignKey(f => f.SourceSnapshotId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
