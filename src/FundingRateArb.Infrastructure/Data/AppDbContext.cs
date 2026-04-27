using FundingRateArb.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FundingRateArb.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    // SQL Server datetime2 strips DateTimeKind. Without this converter, values come
    // back as Unspecified and System.Text.Json serializes them without the "Z"
    // suffix, causing JS clients to interpret UTC timestamps as browser-local time.
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcDateTimeConverter = new(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

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
    public DbSet<CoinGlassExchangeRate> CoinGlassExchangeRates => Set<CoinGlassExchangeRate>();
    public DbSet<CoinGlassDiscoveryEvent> CoinGlassDiscoveryEvents => Set<CoinGlassDiscoveryEvent>();
    public DbSet<AssetExchangeFundingInterval> AssetExchangeFundingIntervals => Set<AssetExchangeFundingInterval>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        builder.Entity<ArbitragePosition>(entity =>
        {
            entity.Property(p => p.LongIntendedMidAtSubmit).HasPrecision(18, 4);
            entity.Property(p => p.ShortIntendedMidAtSubmit).HasPrecision(18, 4);
            entity.Property(p => p.LongEntrySlippagePct).HasPrecision(18, 8);
            entity.Property(p => p.ShortEntrySlippagePct).HasPrecision(18, 8);
            entity.Property(p => p.LongExitSlippagePct).HasPrecision(18, 8);
            entity.Property(p => p.ShortExitSlippagePct).HasPrecision(18, 8);
        });

        builder.Entity<BotConfiguration>(entity =>
        {
            entity.Property(p => p.MaxAcceptableSlippagePct).HasPrecision(18, 8);
        });

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(UtcDateTimeConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(NullableUtcDateTimeConverter);
                }
            }
        }
    }
}
