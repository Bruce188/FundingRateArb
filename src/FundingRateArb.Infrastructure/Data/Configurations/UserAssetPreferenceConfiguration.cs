using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class UserAssetPreferenceConfiguration : IEntityTypeConfiguration<UserAssetPreference>
{
    public void Configure(EntityTypeBuilder<UserAssetPreference> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.UserId).IsRequired();

        builder.HasIndex(p => new { p.UserId, p.AssetId }).IsUnique();

        builder.HasOne(p => p.User)
            .WithMany(u => u.AssetPreferences)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Asset)
            .WithMany()
            .HasForeignKey(p => p.AssetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
