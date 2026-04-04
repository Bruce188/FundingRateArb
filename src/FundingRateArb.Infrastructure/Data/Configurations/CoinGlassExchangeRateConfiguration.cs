using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class CoinGlassExchangeRateConfiguration : IEntityTypeConfiguration<CoinGlassExchangeRate>
{
    public void Configure(EntityTypeBuilder<CoinGlassExchangeRate> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.SourceExchange).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Symbol).HasMaxLength(50).IsRequired();

        builder.Property(r => r.RawRate).HasColumnType("decimal(18,10)");
        builder.Property(r => r.RatePerHour).HasColumnType("decimal(18,10)");
        builder.Property(r => r.MarkPrice).HasColumnType("decimal(18,4)");
        builder.Property(r => r.IndexPrice).HasColumnType("decimal(18,4)");
        builder.Property(r => r.Volume24hUsd).HasColumnType("decimal(18,2)");

        builder.HasIndex(r => new { r.SourceExchange, r.Symbol, r.SnapshotTime })
            .IsUnique();
        builder.HasIndex(r => r.SnapshotTime);
    }
}
