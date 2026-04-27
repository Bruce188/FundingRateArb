using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace FundingRateArb.Tests.Integration.Areas.Admin;

/// <summary>
/// Integration tests for the Status page controller: authz contract and full round-trip
/// against an in-memory database with seeded positions.
/// </summary>
[Collection("IntegrationTests")]
public class StatusControllerIntegrationTests
{
    [Fact]
    public async Task Get_AsAnonymous_RejectsUnauthenticated()
    {
        await using var factory = new StatusPageFactory();
        SeedMinimal(factory);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/Admin/Status");

        // The test auth scheme returns NoResult for unauthenticated requests.
        // The test auth pipeline produces 401 (challenge) rather than 302 (cookie redirect).
        // Both indicate the user is not authenticated — either is acceptable.
        var code = (int)response.StatusCode;
        code.Should().BeOneOf(new[] { 302, 401 },
            "anonymous requests must not reach the Admin/Status endpoint");
    }

    [Fact]
    public async Task Get_AsAuthenticatedNonAdmin_Returns403Forbidden()
    {
        await using var factory = new StatusPageFactory();
        SeedMinimal(factory);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        // "User" role header triggers non-admin auth ticket in TestAuthHandler.
        client.DefaultRequestHeaders.Add("X-Test-Auth", "User");

        var response = await client.GetAsync("/Admin/Status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "authenticated users without the Admin role must receive 403");
    }

    [Fact]
    public async Task Get_AsAdmin_Returns200WithAllSectionNames()
    {
        await using var factory = new StatusPageFactory();
        SeedRich(factory);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Auth", "Admin");

        var response = await client.GetAsync("/Admin/Status");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html[..Math.Min(html.Length, 3000)]);

        // All nine section names must appear in the rendered HTML.
        html.Should().Contain("data-section-name=\"bot-state-header\"");
        html.Should().Contain("data-section-name=\"pnl-attribution\"");
        html.Should().Contain("data-section-name=\"hold-time-distribution\"");
        html.Should().Contain("data-section-name=\"phantom-fee-indicator\"");
        html.Should().Contain("data-section-name=\"per-pair-pnl\"");
        html.Should().Contain("data-section-name=\"per-asset-fee-drag\"");
        html.Should().Contain("data-section-name=\"failed-open-events\"");
        html.Should().Contain("data-section-name=\"skip-reasons\"");
        html.Should().Contain("data-section-name=\"reconciliation\"");
    }

    // ── Seeding helpers ──────────────────────────────────────────────

    private static void SeedMinimal(StatusPageFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        if (!db.BotConfigurations.Any())
        {
            db.BotConfigurations.Add(new BotConfiguration { IsEnabled = false, UpdatedByUserId = "seed" });
        }
        db.SaveChanges();
    }

    private static void SeedRich(StatusPageFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        if (!db.BotConfigurations.Any())
        {
            db.BotConfigurations.Add(new BotConfiguration { IsEnabled = true, UpdatedByUserId = "seed" });
        }

        var hl = new Exchange { Name = "Hyperliquid", ApiBaseUrl = "h", WsBaseUrl = "h" };
        var aster = new Exchange { Name = "Aster", ApiBaseUrl = "a", WsBaseUrl = "a" };
        var btc = new Asset { Symbol = "BTC", Name = "Bitcoin" };
        var user = new ApplicationUser { Id = "seed-user", UserName = "seed", Email = "seed@test" };
        db.Exchanges.AddRange(hl, aster);
        db.Assets.Add(btc);
        db.Users.Add(user);
        db.SaveChanges();

        var now = DateTime.UtcNow;
        // Seed closed positions covering multiple hold-time buckets and PnL windows.
        var positions = new[]
        {
            // < 60s bucket
            new ArbitragePosition
            {
                UserId = user.Id, AssetId = btc.Id, LongExchangeId = hl.Id, ShortExchangeId = aster.Id,
                Status = PositionStatus.Closed, SizeUsdc = 100m, MarginUsdc = 100m, Leverage = 1,
                OpenedAt = now.AddDays(-1), ClosedAt = now.AddDays(-1).AddSeconds(30),
                RealizedPnl = 10m, AccumulatedFunding = 15m, EntryFeesUsdc = 2m, ExitFeesUsdc = 2m,
            },
            // <5m bucket
            new ArbitragePosition
            {
                UserId = user.Id, AssetId = btc.Id, LongExchangeId = hl.Id, ShortExchangeId = aster.Id,
                Status = PositionStatus.Closed, SizeUsdc = 100m, MarginUsdc = 100m, Leverage = 1,
                OpenedAt = now.AddDays(-2), ClosedAt = now.AddDays(-2).AddSeconds(120),
                RealizedPnl = 5m, AccumulatedFunding = 8m, EntryFeesUsdc = 1m, ExitFeesUsdc = 1m,
            },
            // >=6h bucket
            new ArbitragePosition
            {
                UserId = user.Id, AssetId = btc.Id, LongExchangeId = aster.Id, ShortExchangeId = hl.Id,
                Status = PositionStatus.Closed, SizeUsdc = 100m, MarginUsdc = 100m, Leverage = 1,
                OpenedAt = now.AddDays(-3), ClosedAt = now.AddDays(-3).AddHours(8),
                RealizedPnl = 20m, AccumulatedFunding = 25m, EntryFeesUsdc = 2m, ExitFeesUsdc = 2m,
            },
        };
        db.ArbitragePositions.AddRange(positions);

        // Seed a ReconciliationReport for the reconciliation section.
        db.ReconciliationReports.Add(new ReconciliationReport
        {
            RunAtUtc = now.AddMinutes(-5),
            OverallStatus = "Healthy",
            PerExchangeEquityJson = string.Empty,
            DegradedExchangesJson = string.Empty,
        });

        db.SaveChanges();
    }

    // ── WebApplicationFactory ────────────────────────────────────────

    private sealed class StatusPageFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"StatusPage_{Guid.NewGuid()}";

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
                foreach (var d in efDescriptors) { services.Remove(d); }
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));

                // Stub streaming + bot control + hosted services.
                var streamDescriptors = services.Where(d => d.ServiceType == typeof(IMarketDataStream)).ToList();
                foreach (var d in streamDescriptors) { services.Remove(d); }
                services.AddSingleton<IMarketDataStream>(new StubStream());

                var hostedDescriptors = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                foreach (var d in hostedDescriptors) { services.Remove(d); }

                var botControlDescriptors = services.Where(d => d.ServiceType == typeof(IBotControl)).ToList();
                foreach (var d in botControlDescriptors) { services.Remove(d); }
                services.AddSingleton<IBotControl>(new StubBotControl());

                // Test auth — admin role on "X-Test-Auth: Admin", user role on "X-Test-Auth: User".
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
            if (!Request.Headers.TryGetValue("X-Test-Auth", out var roleHeader) || string.IsNullOrWhiteSpace(roleHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, "testuser"),
                new(ClaimTypes.NameIdentifier, "test-user-id"),
            };

            var role = roleHeader.ToString();
            if (role == "Admin")
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }
            else if (role == "User")
            {
                claims.Add(new Claim(ClaimTypes.Role, "User"));
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
        public Task StartAsync(IEnumerable<string> symbols, System.Threading.CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public System.Threading.Tasks.ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubBotControl : IBotControl
    {
        public bool IsRunning => false;
        public DateTime? LastCycleTime => null;
        public void ClearCooldowns() { }
        public void TriggerImmediateCycle() { }
    }
}
