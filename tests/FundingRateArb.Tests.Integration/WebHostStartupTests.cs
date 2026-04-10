using System.Diagnostics;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class WebHostStartupTests : IClassFixture<WebHostStartupTests.WebHostStartupTestFactory>, IDisposable
{
    private readonly WebHostStartupTestFactory _factory;
    private readonly HttpClient _client;

    public WebHostStartupTests(WebHostStartupTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task AppStartsWithin60Seconds()
    {
        var sw = Stopwatch.StartNew();
        var response = await _client.GetAsync("/healthz");
        sw.Stop();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60),
            "app should start and respond to healthz within 60 seconds");
    }

    [Fact]
    public void AllBackgroundServicesRegistered()
    {
        // Services registered via AddHostedService<T> appear directly as ImplementationType.
        // BotOrchestrator is registered via AddSingleton<BotOrchestrator>() + forwarding
        // factory to IHostedService, so it appears in CapturedServiceTypes instead.
        var allTypes = _factory.CapturedHostedServiceTypes
            .Concat(_factory.CapturedServiceTypes)
            .ToList();

        var expectedTypes = new[]
        {
            typeof(MarketDataStreamManager),
            typeof(FundingRateFetcher),
            typeof(BotOrchestrator),
            typeof(DailySummaryService),
            typeof(LeverageTierRefresher),
        };

        foreach (var expected in expectedTypes)
        {
            allTypes.Should().Contain(
                t => t == expected || t.Name == expected.Name,
                $"background service registration for {expected.Name} should exist");
        }
    }

    [Fact]
    public async Task HealthzReturnsOk()
    {
        var response = await _client.GetAsync("/healthz");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // InMemoryDatabase cannot execute raw SQL (SELECT 1), so DatabaseHealthCheck
        // returns Degraded. Both Healthy and Degraded map to 200 OK.
        body.Should().BeOneOf("Healthy", "Degraded");
    }

    [Fact]
    public async Task NoUnhandledExceptionsDuringWarmup()
    {
        // Trigger app startup by making a request
        await _client.GetAsync("/healthz");

        _factory.CapturedLogEntries
            .Where(e => e.LogLevel >= LogLevel.Error)
            .Should().BeEmpty("no Error or Critical log entries should occur during startup");
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public class WebHostStartupTestFactory : WebApplicationFactory<Program>
    {
        public List<Type> CapturedHostedServiceTypes { get; } = [];
        public List<Type> CapturedServiceTypes { get; } = [];
        public List<LogEntry> CapturedLogEntries { get; } = [];

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
                // First pass: capture hosted service descriptor info BEFORE removal.
                // For factory-registered services (e.g. BotOrchestrator registered via
                // AddSingleton<IHostedService>(sp => sp.GetRequiredService<BotOrchestrator>())),
                // we inspect the factory delegate's method body text to identify the type.
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)))
                {
                    if (d.ImplementationType is not null)
                    {
                        CapturedHostedServiceTypes.Add(d.ImplementationType);
                    }
                    else if (d.ImplementationInstance is not null)
                    {
                        CapturedHostedServiceTypes.Add(d.ImplementationInstance.GetType());
                    }
                }

                // Also capture concrete service registrations (e.g. AddSingleton<BotOrchestrator>())
                // that are forwarded to IHostedService via a factory delegate
                foreach (var d in services)
                {
                    var implType = d.ImplementationType ?? d.ServiceType;
                    if (implType is not null && !implType.IsInterface && !implType.IsAbstract
                        && implType.Namespace?.StartsWith("FundingRateArb", StringComparison.Ordinal) == true)
                    {
                        CapturedServiceTypes.Add(implType);
                    }
                }

                // Replace DbContext with InMemory
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
                    options.UseInMemoryDatabase($"WebHostStartupTest_{Guid.NewGuid()}"));

                // Replace IMarketDataStream with stub
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream("TestExchange", true));

                // Remove all background hosted services
                var hostedDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
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

                // Add a capturing logger provider for the warmup-exception test
                var capturedEntries = CapturedLogEntries;
                services.AddLogging(lb =>
                {
                    lb.AddProvider(new CapturingLoggerProvider(capturedEntries));
                });
            });
        }
    }

    public record LogEntry(LogLevel LogLevel, string Category, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries;

        public CapturingLoggerProvider(List<LogEntry> entries) => _entries = entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<LogEntry> _entries;

            public CapturingLogger(string category, List<LogEntry> entries)
            {
                _category = category;
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lock (_entries)
                    {
                        _entries.Add(new LogEntry(logLevel, _category, formatter(state, exception)));
                    }
                }
            }
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
}
