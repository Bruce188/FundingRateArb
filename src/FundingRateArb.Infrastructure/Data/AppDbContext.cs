using FundingRateArb.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Exchange> Exchanges => Set<Exchange>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<FundingRateSnapshot> FundingRateSnapshots => Set<FundingRateSnapshot>();
    public DbSet<ArbitragePosition> ArbitragePositions => Set<ArbitragePosition>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<BotConfiguration> BotConfigurations => Set<BotConfiguration>();
    public DbSet<ExchangeAssetConfig> ExchangeAssetConfigs => Set<ExchangeAssetConfig>();
    public DbSet<UserExchangeCredential> UserExchangeCredentials => Set<UserExchangeCredential>();
    public DbSet<UserConfiguration> UserConfigurations => Set<UserConfiguration>();
    public DbSet<UserExchangePreference> UserExchangePreferences => Set<UserExchangePreference>();
    public DbSet<UserAssetPreference> UserAssetPreferences => Set<UserAssetPreference>();
    public DbSet<FundingRateHourlyAggregate> FundingRateHourlyAggregates => Set<FundingRateHourlyAggregate>();
    public DbSet<OpportunitySnapshot> OpportunitySnapshots => Set<OpportunitySnapshot>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
