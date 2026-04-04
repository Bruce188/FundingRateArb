using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class ExchangeConfiguration : IEntityTypeConfiguration<Exchange>
{
    public void Configure(EntityTypeBuilder<Exchange> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ApiBaseUrl).IsRequired().HasMaxLength(255);
        builder.Property(e => e.WsBaseUrl).IsRequired().HasMaxLength(255);
        builder.Property(e => e.Description).HasMaxLength(500);

        builder.Property(e => e.TakerFeeRate).HasPrecision(18, 8);
        builder.Property(e => e.FundingRebateRate).HasPrecision(5, 4);

        builder.HasIndex(e => e.Name).IsUnique();
    }
}
