using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Message).IsRequired().HasMaxLength(2000);

        builder.HasOne(a => a.User)
            .WithMany(u => u.Alerts)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ArbitragePosition)
            .WithMany(p => p.Alerts)
            .HasForeignKey(a => a.ArbitragePositionId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        builder.HasIndex(a => new { a.UserId, a.CreatedAt });
        builder.HasIndex(a => new { a.IsRead, a.CreatedAt });

        // Composite index for GetRecentByPositionIdsAsync performance
        builder.HasIndex(a => new { a.ArbitragePositionId, a.Type, a.CreatedAt })
            .HasDatabaseName("IX_Alerts_PositionId_Type_CreatedAt")
            .IsDescending(false, false, true);
    }
}
