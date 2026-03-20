using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class UserExchangePreferenceConfiguration : IEntityTypeConfiguration<UserExchangePreference>
{
    public void Configure(EntityTypeBuilder<UserExchangePreference> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.UserId).IsRequired();

        builder.HasIndex(p => new { p.UserId, p.ExchangeId }).IsUnique();

        builder.HasOne(p => p.User)
            .WithMany(u => u.ExchangePreferences)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Exchange)
            .WithMany()
            .HasForeignKey(p => p.ExchangeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
