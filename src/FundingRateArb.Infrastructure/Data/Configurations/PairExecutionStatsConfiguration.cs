using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class PairExecutionStatsConfiguration : IEntityTypeConfiguration<PairExecutionStats>
{
    public void Configure(EntityTypeBuilder<PairExecutionStats> entity)
    {
        entity.ToTable("PairExecutionStats");
        entity.HasKey(p => p.Id);
        entity.Property(p => p.LongExchangeName).HasMaxLength(64).IsRequired();
        entity.Property(p => p.ShortExchangeName).HasMaxLength(64).IsRequired();
        entity.Property(p => p.DeniedReason).HasMaxLength(128).IsRequired(false);
        entity.Property(p => p.WindowStart).IsRequired();
        entity.Property(p => p.WindowEnd).IsRequired();
        entity.Property(p => p.LastUpdatedAt).IsRequired();
        // Index/precision configured in AppDbContext.OnModelCreating (Task 3.1).
    }
}
