using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class UserConfigurationConfiguration : IEntityTypeConfiguration<UserConfiguration>
{
    public void Configure(EntityTypeBuilder<UserConfiguration> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UserId).IsRequired();

        builder.Property(c => c.OpenThreshold).HasColumnType("decimal(18,10)");
        builder.Property(c => c.CloseThreshold).HasColumnType("decimal(18,10)");
        builder.Property(c => c.AlertThreshold).HasColumnType("decimal(18,10)");
        builder.Property(c => c.TotalCapitalUsdc).HasColumnType("decimal(18,2)");
        builder.Property(c => c.MaxCapitalPerPosition).HasColumnType("decimal(18,4)");
        builder.Property(c => c.StopLossPct).HasColumnType("decimal(18,4)");
        builder.Property(c => c.FeeAmortizationHours).HasColumnType("decimal(18,2)");
        builder.Property(c => c.MinPositionSizeUsdc).HasColumnType("decimal(18,2)");
        builder.Property(c => c.MinVolume24hUsdc).HasColumnType("decimal(18,2)");
        builder.Property(c => c.DailyDrawdownPausePct).HasColumnType("decimal(18,4)");
        builder.Property(c => c.MaxExposurePerAsset).HasColumnType("decimal(18,4)");
        builder.Property(c => c.MaxExposurePerExchange).HasColumnType("decimal(18,4)");
        builder.Property(c => c.RotationThresholdPerHour).HasColumnType("decimal(18,10)").HasDefaultValue(0.0003m);
        builder.Property(c => c.MinHoldBeforeRotationMinutes).HasDefaultValue(30);
        builder.Property(c => c.MaxRotationsPerDay).HasDefaultValue(5);
        builder.Property(c => c.DryRunEnabled).HasDefaultValue(false);

        builder.HasIndex(c => c.UserId).IsUnique();

        builder.HasOne(c => c.User)
            .WithMany(u => u.Configurations)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
