using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FundingRateArb.Tests.Integration.Areas.Admin;

/// <summary>
/// AC5(d) + AC6 verification — render the Admin Overview slippage rollup, the
/// empty-state message, and round-trip persist all six new fields against a
/// real relational provider (SQLite in-memory).
/// </summary>
[Collection("IntegrationTests")]
public class AdminOverviewSlippageSectionTests
{
    [Fact]
    public async Task Index_WithSeededClosedPositions_RendersSlippageRollup()
    {
        await using var factory = new SlippageOverviewFactory();
        Seed(factory, includeClosedInWindow: true);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Auth", "Admin");

        var response = await client.GetAsync("/Admin/Overview");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html[..Math.Min(html.Length, 2000)]);

        html.Should().Contain("Slippage Attribution",
            "the section header must render when closed positions exist in the rolling window");
        html.Should().Contain("Hyperliquid / Lighter",
            "the rollup must include the Hyperliquid/Lighter pair label from the seeded SOL positions");
        html.Should().Contain("Aster / Lighter",
            "the rollup must include the Aster/Lighter pair label from the seeded ETH position");
        html.Should().Contain("SOL", "asset rollup must include the SOL row");
        html.Should().Contain("ETH", "asset rollup must include the ETH row");

        // Long-leg entry slippage average for SOL on Hyperliquid+Lighter is (0.0010 + 0.0030) / 2 = 0.0020.
        var expected = 0.0020m.ToString("P4", CultureInfo.InvariantCulture);
        html.Should().Contain(expected,
            $"the rendered HTML must include the computed average pct '{expected}'");
    }

    [Fact]
    public async Task Index_NoClosedPositionsInWindow_RendersEmptyState()
    {
        await using var factory = new SlippageOverviewFactory();
        Seed(factory, includeClosedInWindow: false);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Auth", "Admin");

        var response = await client.GetAsync("/Admin/Overview");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html[..Math.Min(html.Length, 2000)]);
        html.Should().Contain("No closed positions in last 7 days.",
            "the empty-state literal must render when no closed positions exist in the window");
    }

    [Fact]
    public async Task Migration_AppliesCleanly_FromTipOfMain()
    {
        // Use SQLite in-memory as the relational provider so the schema is exercised end-to-end
        // (InMemoryDatabase skips migrations; SQLite EnsureCreated executes the model and surfaces
        // any precision/null-constraint mismatches).
        await using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var ctx = new AppDbContext(options))
        {
            (await ctx.Database.EnsureCreatedAsync()).Should().BeTrue();
        }

        await using (var ctx = new AppDbContext(options))
        {
            var user = new ApplicationUser { Id = "u1", UserName = "u1", Email = "u1@test" };
            var asset = new Asset { Symbol = "BTC", Name = "Bitcoin" };
            var exchange = new Exchange { Name = "TestX", ApiBaseUrl = "x", WsBaseUrl = "x" };
            ctx.Users.Add(user);
            ctx.Assets.Add(asset);
            ctx.Exchanges.Add(exchange);
            await ctx.SaveChangesAsync();

            var pos = new ArbitragePosition
            {
                UserId = user.Id,
                AssetId = asset.Id,
                LongExchangeId = exchange.Id,
                ShortExchangeId = exchange.Id,
                SizeUsdc = 100m,
                MarginUsdc = 100m,
                Leverage = 1,
                LongEntryPrice = 100m,
                ShortEntryPrice = 100m,
                EntrySpreadPerHour = 0.0005m,
                CurrentSpreadPerHour = 0.0005m,
                Status = PositionStatus.Closed,
                OpenedAt = DateTime.UtcNow.AddHours(-2),
                ClosedAt = DateTime.UtcNow.AddHours(-1),
                LongIntendedMidAtSubmit = 100.5000m,
                ShortIntendedMidAtSubmit = 100.5500m,
                LongEntrySlippagePct = 0.00012345m,
                ShortEntrySlippagePct = -0.00012345m,
                LongExitSlippagePct = 0.00067890m,
                ShortExitSlippagePct = 0.00000001m,
            };
            ctx.ArbitragePositions.Add(pos);
            await ctx.SaveChangesAsync();
        }

        // Round-trip — read back the persisted values and assert all six fields survive insert+query.
        await using (var ctx = new AppDbContext(options))
        {
            var loaded = await ctx.ArbitragePositions.SingleAsync();

            loaded.LongIntendedMidAtSubmit.Should().Be(100.5000m);
            loaded.ShortIntendedMidAtSubmit.Should().Be(100.5500m);
            loaded.LongEntrySlippagePct.Should().Be(0.00012345m);
            loaded.ShortEntrySlippagePct.Should().Be(-0.00012345m);
            loaded.LongExitSlippagePct.Should().Be(0.00067890m);
            loaded.ShortExitSlippagePct.Should().Be(0.00000001m);
        }
    }

    // ── Seeding helpers ──────────────────────────────────────────────

    private static void Seed(SlippageOverviewFactory factory, bool includeClosedInWindow)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        if (!db.BotConfigurations.Any())
        {
            db.BotConfigurations.Add(new BotConfiguration
            {
                IsEnabled = false,
                UpdatedByUserId = "seed",
            });
        }

        var hl = new Exchange { Name = "Hyperliquid", ApiBaseUrl = "h", WsBaseUrl = "h" };
        var lighter = new Exchange { Name = "Lighter", ApiBaseUrl = "l", WsBaseUrl = "l" };
        var aster = new Exchange { Name = "Aster", ApiBaseUrl = "a", WsBaseUrl = "a" };
        var sol = new Asset { Symbol = "SOL", Name = "Solana" };
        var eth = new Asset { Symbol = "ETH", Name = "Ethereum" };
        var user = new ApplicationUser { Id = "seed-user", UserName = "seed", Email = "seed@test" };
        db.Exchanges.AddRange(hl, lighter, aster);
        db.Assets.AddRange(sol, eth);
        db.Users.Add(user);
        db.SaveChanges();

        var now = DateTime.UtcNow;
        // Two closed SOL positions on Hyperliquid+Lighter; one closed ETH on Aster+Lighter.
        // Position counts and slippage values are deterministic for HTML assertion.
        var openedHours = includeClosedInWindow ? -2 : -24 * 30;
        var closedHours = includeClosedInWindow ? -1 : -24 * 29;

        db.ArbitragePositions.AddRange(
            new ArbitragePosition
            {
                UserId = user.Id,
                AssetId = sol.Id,
                LongExchangeId = hl.Id,
                ShortExchangeId = lighter.Id,
                Status = PositionStatus.Closed,
                SizeUsdc = 100m,
                MarginUsdc = 100m,
                Leverage = 1,
                OpenedAt = now.AddHours(openedHours),
                ClosedAt = now.AddHours(closedHours),
                LongEntrySlippagePct = 0.0010m,
                ShortEntrySlippagePct = 0.0020m,
                LongExitSlippagePct = 0.0030m,
                ShortExitSlippagePct = 0.0040m,
            },
            new ArbitragePosition
            {
                UserId = user.Id,
                AssetId = sol.Id,
                LongExchangeId = hl.Id,
                ShortExchangeId = lighter.Id,
                Status = PositionStatus.Closed,
                SizeUsdc = 100m,
                MarginUsdc = 100m,
                Leverage = 1,
                OpenedAt = now.AddHours(openedHours - 1),
                ClosedAt = now.AddHours(closedHours - 1),
                LongEntrySlippagePct = 0.0030m,
                ShortEntrySlippagePct = 0.0040m,
                LongExitSlippagePct = 0.0050m,
                ShortExitSlippagePct = 0.0060m,
            },
            new ArbitragePosition
            {
                UserId = user.Id,
                AssetId = eth.Id,
                LongExchangeId = aster.Id,
                ShortExchangeId = lighter.Id,
                Status = PositionStatus.Closed,
                SizeUsdc = 100m,
                MarginUsdc = 100m,
                Leverage = 1,
                OpenedAt = now.AddHours(openedHours - 2),
                ClosedAt = now.AddHours(closedHours - 2),
                LongEntrySlippagePct = 0.0005m,
                ShortEntrySlippagePct = 0.0015m,
                LongExitSlippagePct = 0.0025m,
                ShortExitSlippagePct = 0.0035m,
            });

        db.SaveChanges();
    }

    // ── WebApplicationFactory ────────────────────────────────────────

    private sealed class SlippageOverviewFactory : WebApplicationFactory<Program>
    {
        // Per-instance to keep each test isolated — multiple factories must not share an InMemory DB.
        private readonly string _dbName = $"AdminSlippageOverview_{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "not-used",
                    ["Seed:AdminPassword"] = "Test@Password1234!",
                });
            });

            builder.ConfigureServices(services =>
            {
                // Replace EF with InMemory.
                var efDescriptors = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                             || d.ServiceType == typeof(AppDbContext)
                             || (d.ServiceType.IsGenericType &&
                                 d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>)))
                    .ToList();
                foreach (var d in efDescriptors)
                {
                    services.Remove(d);
                }
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));

                // Stub IMarketDataStream / IBotControl / hosted services.
                var streamDescriptors = services.Where(d => d.ServiceType == typeof(IMarketDataStream)).ToList();
                foreach (var d in streamDescriptors) { services.Remove(d); }
                services.AddSingleton<IMarketDataStream>(new StubStream());

                var hostedDescriptors = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                foreach (var d in hostedDescriptors) { services.Remove(d); }

                var botControlDescriptors = services.Where(d => d.ServiceType == typeof(IBotControl)).ToList();
                foreach (var d in botControlDescriptors) { services.Remove(d); }
                services.AddSingleton<IBotControl>(new StubBotControl());

                // Test auth — admin role on "X-Test-Auth: Admin".
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Auth", out var role) || string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, "testadmin"),
                new(ClaimTypes.NameIdentifier, "test-user-id"),
            };

            if (role == "Admin")
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }

            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class StubStream : IMarketDataStream
    {
        public string ExchangeName => "TestExchange";
        public bool IsConnected => true;
#pragma warning disable CS0067
        public event Action<FundingRateDto>? OnRateUpdate;
        public event Action<string, string>? OnDisconnected;
#pragma warning restore CS0067
        public Task StartAsync(IEnumerable<string> symbols, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubBotControl : IBotControl
    {
        public bool IsRunning => false;
        public DateTime? LastCycleTime => null;
        public void ClearCooldowns() { }
        public void TriggerImmediateCycle() { }
    }
}
