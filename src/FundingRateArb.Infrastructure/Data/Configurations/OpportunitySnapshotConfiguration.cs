using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class OpportunitySnapshotConfiguration : IEntityTypeConfiguration<OpportunitySnapshot>
{
    public void Configure(EntityTypeBuilder<OpportunitySnapshot> builder)
    {
        builder.HasKey(s => s.Id);

        builder.Property(s => s.SpreadPerHour).HasPrecision(18, 10);
        builder.Property(s => s.NetYieldPerHour).HasPrecision(18, 10);
        builder.Property(s => s.LongVolume24h).HasPrecision(18, 2);
        builder.Property(s => s.ShortVolume24h).HasPrecision(18, 2);
        builder.Property(s => s.SkipReason).HasMaxLength(50);

        // Index for time-range queries and purge
        builder.HasIndex(s => s.RecordedAt);

        // Composite index for analytics queries by asset
        builder.HasIndex(s => new { s.AssetId, s.RecordedAt });

        builder.HasOne(s => s.Asset)
               .WithMany()
               .HasForeignKey(s => s.AssetId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.LongExchange)
               .WithMany()
               .HasForeignKey(s => s.LongExchangeId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.ShortExchange)
               .WithMany()
               .HasForeignKey(s => s.ShortExchangeId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
