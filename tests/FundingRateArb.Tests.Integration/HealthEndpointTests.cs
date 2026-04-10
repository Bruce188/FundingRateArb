using System.Net.Http.Json;
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
public class HealthEndpointTests : IClassFixture<HealthEndpointTests.HealthTestFactory>, IDisposable
{
    private readonly HealthTestFactory _factory;
    private readonly HttpClient _client;

    public HealthEndpointTests(HealthTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task GetHealthz_ReturnsOk_WithoutAuth()
    {
        var response = await _client.GetAsync("/healthz");

        // healthz returns aggregate status: 200 if Healthy/Degraded, 503 if Unhealthy
        // With stub stream connected, expect 200
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // InMemoryDatabase cannot execute raw SQL (SELECT 1), so DatabaseHealthCheck
        // returns Degraded. Both Healthy and Degraded map to 200 OK.
        body.Should().BeOneOf("Healthy", "Degraded");
    }

    [Fact]
    public async Task GetHealth_RequiresAuthorization()
    {
        var response = await _client.GetAsync("/health");

        // Unauthenticated request should be redirected to login (302)
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task GetHealth_Authenticated_ReturnsJsonWithExpectedStructure()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        client.DefaultRequestHeaders.Add("X-Test-Auth", "true");
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadFromJsonAsync<HealthResponse>();
        json.Should().NotBeNull();
        json!.Status.Should().NotBeNullOrEmpty();
        json.TotalDuration.Should().BeGreaterOrEqualTo(0);
        json.Entries.Should().NotBeNull();
        json.Entries.Should().ContainSingle(e => e.Name == "websocket-streams");
        var wsEntry = json.Entries.First(e => e.Name == "websocket-streams");
        wsEntry.Status.Should().NotBeNullOrEmpty();
        wsEntry.Description.Should().NotBeNullOrEmpty();
        wsEntry.Duration.Should().BeGreaterOrEqualTo(0);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public class HealthTestFactory : WebApplicationFactory<Program>
    {
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
                // Remove all DbContext registrations that use SQL Server
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
                    options.UseInMemoryDatabase($"HealthTest_{Guid.NewGuid()}"));

                // Remove real IMarketDataStream registrations and replace with stub
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream("TestExchange", true));

                // Remove all background hosted services to avoid exchange/DB dependencies in tests
                var hostedDescriptors = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                foreach (var d in hostedDescriptors)
                {
                    services.Remove(d);
                }

                // Remove IBotControl which depends on BotOrchestrator
                var botControlDescriptors = services
                    .Where(d => d.ServiceType == typeof(IBotControl))
                    .ToList();
                foreach (var d in botControlDescriptors)
                {
                    services.Remove(d);
                }
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
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class StubMarketDataStream : IMarketDataStream
    {
        public StubMarketDataStream(string exchangeName, bool isConnected)
        {
            ExchangeName = exchangeName;
            IsConnected = isConnected;
        }

        public string ExchangeName { get; }
        public bool IsConnected { get; }

#pragma warning disable CS0067 // Events required by interface but unused in stub
        public event Action<FundingRateDto>? OnRateUpdate;
        public event Action<string, string>? OnDisconnected;
#pragma warning restore CS0067

        public Task StartAsync(IEnumerable<string> symbols, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record HealthResponse(
        string Status,
        double TotalDuration,
        HealthEntry[] Entries);

    private sealed record HealthEntry(
        string Name,
        string Status,
        string Description,
        double Duration);
}
