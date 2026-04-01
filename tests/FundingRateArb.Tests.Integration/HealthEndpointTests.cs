using System.Text.Json;
using FluentAssertions;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Tests.Integration;

public class HealthEndpointTests : IClassFixture<HealthEndpointTests.HealthTestFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(HealthTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_ReturnsJsonWithExpectedStructure()
    {
        var response = await _client.GetAsync("/health");

        // Health endpoint returns 200 (Healthy/Degraded) or 503 (Unhealthy)
        // In test mode streams are disconnected, so 503 is expected
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("status", out var statusProp).Should().BeTrue();
        statusProp.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("totalDuration", out _).Should().BeTrue();

        root.TryGetProperty("entries", out var entriesProp).Should().BeTrue();
        entriesProp.ValueKind.Should().Be(JsonValueKind.Array);

        // Each entry should have name, status, and duration
        foreach (var entry in entriesProp.EnumerateArray())
        {
            entry.TryGetProperty("name", out _).Should().BeTrue();
            entry.TryGetProperty("status", out _).Should().BeTrue();
            entry.TryGetProperty("duration", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetHealth_EntriesContainExpectedChecks()
    {
        var response = await _client.GetAsync("/health");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var entries = doc.RootElement.GetProperty("entries");
        var names = entries.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        names.Should().Contain("websocket-streams");
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
                    ["ConnectionStrings:DefaultConnection"] = "not-used"
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
                    options.UseInMemoryDatabase("HealthTest"));
            });
        }
    }
}
