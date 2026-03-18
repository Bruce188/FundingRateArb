using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(100);

        builder.HasIndex(a => a.Symbol).IsUnique();
    }
}
