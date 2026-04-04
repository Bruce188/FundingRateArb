using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class CoinGlassDiscoveryEventConfiguration : IEntityTypeConfiguration<CoinGlassDiscoveryEvent>
{
    public void Configure(EntityTypeBuilder<CoinGlassDiscoveryEvent> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ExchangeName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Symbol).HasMaxLength(50);

        builder.HasIndex(e => e.DiscoveredAt);
    }
}
