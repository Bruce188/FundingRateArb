using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class UserExchangeCredentialConfiguration : IEntityTypeConfiguration<UserExchangeCredential>
{
    public void Configure(EntityTypeBuilder<UserExchangeCredential> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UserId).IsRequired();
        builder.Property(c => c.EncryptedApiKey).HasMaxLength(2000);
        builder.Property(c => c.EncryptedApiSecret).HasMaxLength(2000);
        builder.Property(c => c.EncryptedWalletAddress).HasMaxLength(2000);
        builder.Property(c => c.EncryptedPrivateKey).HasMaxLength(2000);

        builder.HasIndex(c => new { c.UserId, c.ExchangeId }).IsUnique();

        builder.HasOne(c => c.User)
            .WithMany(u => u.ExchangeCredentials)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.Exchange)
            .WithMany()
            .HasForeignKey(c => c.ExchangeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
