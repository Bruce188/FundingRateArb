using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class ExchangeAssetConfigConfiguration : IEntityTypeConfiguration<ExchangeAssetConfig>
{
    public void Configure(EntityTypeBuilder<ExchangeAssetConfig> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.MinOrderSize).HasColumnType("decimal(18,4)");
        builder.Property(c => c.StepSize).HasColumnType("decimal(18,10)");

        builder.HasOne(c => c.Exchange)
            .WithMany()
            .HasForeignKey(c => c.ExchangeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.Asset)
            .WithMany()
            .HasForeignKey(c => c.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.ExchangeId, c.AssetId }).IsUnique();
    }
}
