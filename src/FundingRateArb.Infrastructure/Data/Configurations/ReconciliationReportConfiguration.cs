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
        // Unbounded text columns. EF Core maps `string` (no max length) to provider-appropriate
        // unlimited-text types: `nvarchar(max)` on SQL Server, `TEXT` on SQLite. Specifying
        // `nvarchar(max)` explicitly breaks SQLite-backed unit tests (`EnsureCreatedAsync`
        // emits literal CREATE TABLE SQL with `nvarchar(max)` which SQLite cannot parse).
        entity.Property(r => r.PerExchangeEquityJson).IsRequired();
        entity.Property(r => r.DegradedExchangesJson).IsRequired();
        entity.Property(r => r.AnomalySummary).HasMaxLength(4000).IsRequired(false);
        entity.HasIndex(r => r.RunAtUtc).IsDescending();
    }
}
