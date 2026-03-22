using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class FundingRateHourlyAggregateConfiguration : IEntityTypeConfiguration<FundingRateHourlyAggregate>
{
    public void Configure(EntityTypeBuilder<FundingRateHourlyAggregate> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.AvgRatePerHour).HasPrecision(18, 10);
        builder.Property(a => a.MinRate).HasPrecision(18, 10);
        builder.Property(a => a.MaxRate).HasPrecision(18, 10);
        builder.Property(a => a.LastRate).HasPrecision(18, 10);
        builder.Property(a => a.AvgVolume24hUsd).HasPrecision(18, 2);
        builder.Property(a => a.AvgMarkPrice).HasPrecision(18, 8);

        // Composite index for efficient range queries by exchange/asset/hour
        builder.HasIndex(a => new { a.ExchangeId, a.AssetId, a.HourUtc })
               .IsUnique();

        builder.HasOne(a => a.Exchange)
               .WithMany()
               .HasForeignKey(a => a.ExchangeId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Asset)
               .WithMany()
               .HasForeignKey(a => a.AssetId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
