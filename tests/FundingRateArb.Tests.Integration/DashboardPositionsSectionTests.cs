using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
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

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class DashboardPositionsSectionTests : IClassFixture<DashboardPositionsSectionTests.DashboardTestFactory>, IDisposable
{
    private readonly HttpClient _client;

    public DashboardPositionsSectionTests(DashboardTestFactory factory)
    {
        // Seed required data before creating client
        SeedData(factory);

        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        _client.DefaultRequestHeaders.Add("X-Test-Auth", "true");
    }

    private static void SeedData(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        if (!db.BotConfigurations.Any())
        {
            db.BotConfigurations.Add(new FundingRateArb.Domain.Entities.BotConfiguration
            {
                IsEnabled = false,
                UpdatedByUserId = "seed"
            });
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task Dashboard_AuthenticatedWithZeroPositions_RendersPositionsContainer()
    {
        var response = await _client.GetAsync("/");

        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, html[..Math.Min(html.Length, 2000)]);

        html.Should().Contain("id=\"positions-section\"",
            "positions section container should always be in DOM for authenticated users");
        html.Should().Contain("id=\"positions-table\"",
            "positions table should always be in DOM for authenticated users");
        html.Should().Contain("id=\"positions-cards\"",
            "positions cards should always be in DOM for authenticated users");
        html.Should().Contain("id=\"positions-empty\"",
            "empty-state placeholder should be present when no positions exist");

        // Section must be hidden (d-none) when there are zero positions
        html.Should().Match("*id=\"positions-section\"*d-none*",
            "positions section must have d-none class when there are zero positions");
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public class DashboardTestFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = $"DashboardTest_{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "not-used",
                    ["Seed:AdminPassword"] = "Test@Password1234!"
                });
            });

            builder.ConfigureServices(services =>
            {
                // Replace DB with InMemory
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

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(DbName));

                // Remove real stream registrations
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton<IMarketDataStream>(new StubStream());

                // Remove background hosted services
                var hostedDescriptors = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                foreach (var d in hostedDescriptors)
                {
                    services.Remove(d);
                }

                // Remove and replace IBotControl
                var botControlDescriptors = services
                    .Where(d => d.ServiceType == typeof(IBotControl))
                    .ToList();
                foreach (var d in botControlDescriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton<IBotControl>(new StubBotControl());

                // Add test authentication
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
            if (!Request.Headers.ContainsKey("X-Test-Auth"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "testadmin"),
                new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
                new Claim(ClaimTypes.Role, "Admin")
            };
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
