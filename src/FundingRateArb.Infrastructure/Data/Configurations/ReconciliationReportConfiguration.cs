using FundingRateArb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundingRateArb.Infrastructure.Data.Configurations;

public class ReconciliationReportConfiguration : IEntityTypeConfiguration<ReconciliationReport>
{
    public void Configure(EntityTypeBuilder<ReconciliationReport> entity)
    {
        entity.ToTable("ReconciliationReports");
        entity.HasKey(r => r.Id);
        entity.Property(r => r.RunAtUtc).IsRequired();
        entity.Property(r => r.OverallStatus).HasMaxLength(32).IsRequired();
        entity.Property(r => r.PerExchangeEquityJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(r => r.DegradedExchangesJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(r => r.AnomalySummary).HasMaxLength(4000).IsRequired(false);
        entity.HasIndex(r => r.RunAtUtc).IsDescending();
    }
}
