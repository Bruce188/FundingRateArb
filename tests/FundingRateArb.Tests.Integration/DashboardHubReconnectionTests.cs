using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class DashboardHubReconnectionTests : IClassFixture<DashboardHubReconnectionTests.HubTestFactory>, IAsyncDisposable
{
    private readonly HubTestFactory _factory;
    private readonly HubConnection _hubConnection;

    public DashboardHubReconnectionTests(HubTestFactory factory)
    {
        _factory = factory;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/dashboard",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Headers.Add("X-Test-Auth", "admin");
                })
            .Build();
    }

    [Fact]
    public async Task RejoinGroups_CompletesWithoutError()
    {
        await _hubConnection.StartAsync();

        var act = async () => await _hubConnection.InvokeAsync("RejoinGroups");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RejoinGroups_RejoinsMarketDataGroup_ReceivesGroupBroadcast()
    {
        await _hubConnection.StartAsync();

        // Rejoin groups
        await _hubConnection.InvokeAsync("RejoinGroups");

        // Set up handler to receive broadcast
        var tcs = new TaskCompletionSource<DashboardDto>();
        _hubConnection.On<DashboardDto>("ReceiveDashboardUpdate", dto => tcs.TrySetResult(dto));

        // Broadcast to MarketData group via IHubContext
        var hubContext = _factory.Services
            .GetRequiredService<IHubContext<DashboardHub, IDashboardClient>>();
        await hubContext.Clients.Group(HubGroups.MarketData)
            .ReceiveDashboardUpdate(new DashboardDto { OpenPositionCount = 42 });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.OpenPositionCount.Should().Be(42);
    }

    [Fact]
    public async Task RejoinGroups_NonAdmin_DoesNotReceiveAdminGroupMessages()
    {
        // Create a non-admin connection
        await using var nonAdminConnection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/dashboard",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Headers.Add("X-Test-Auth", "user");
                })
            .Build();

        await nonAdminConnection.StartAsync();
        await nonAdminConnection.InvokeAsync("RejoinGroups");

        var received = false;
        nonAdminConnection.On<DashboardDto>("ReceiveDashboardUpdate", _ => received = true);

        // Broadcast to Admins group only
        var hubContext = _factory.Services
            .GetRequiredService<IHubContext<DashboardHub, IDashboardClient>>();
        await hubContext.Clients.Group(HubGroups.Admins)
            .ReceiveDashboardUpdate(new DashboardDto { OpenPositionCount = 99 });

        // Wait briefly — non-admin should NOT receive the message
        await Task.Delay(500);
        received.Should().BeFalse("non-admin should not be in the Admins group");

        await nonAdminConnection.StopAsync();
    }

    [Fact]
    public async Task RequestFullUpdate_SendsDashboardDto()
    {
        await _hubConnection.StartAsync();

        var tcs = new TaskCompletionSource<DashboardDto>();
        _hubConnection.On<DashboardDto>("ReceiveDashboardUpdate", dto => tcs.SetResult(dto));

        await _hubConnection.InvokeAsync("RequestFullUpdate");

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        received.Should().NotBeNull();
        received.OpenPositionCount.Should().BeGreaterThanOrEqualTo(0);
        received.NeedsAttentionCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task RequestFullUpdate_WithoutNameIdentifier_ThrowsHubException()
    {
        // Connect with an identity that has no NameIdentifier claim
        await using var noIdConnection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/dashboard",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Headers.Add("X-Test-Auth", "no-identifier");
                })
            .Build();

        await noIdConnection.StartAsync();

        var act = async () => await noIdConnection.InvokeAsync("RequestFullUpdate");

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.WithMessage("*Authentication required*");

        await noIdConnection.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _hubConnection.DisposeAsync();
    }

    public class HubTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"HubTest_{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "not-used",
                    ["Seed:AdminPassword"] = "NotUsed-InMemoryDb"
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
                    options.UseInMemoryDatabase(_dbName));

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

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            if (!db.BotConfigurations.Any())
            {
                db.BotConfigurations.Add(new BotConfiguration
                {
                    IsEnabled = false,
                    UpdatedByUserId = "seed"
                });
                db.SaveChanges();
            }
            return host;
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
            if (!Request.Headers.TryGetValue("X-Test-Auth", out var authValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var authMode = authValues.FirstOrDefault() ?? "";

            if (authMode == "no-identifier")
            {
                // Authenticated but no NameIdentifier — for testing auth-required paths
                var minimalClaims = new[]
                {
                    new Claim(ClaimTypes.Name, "anonymous")
                };
                var minimalIdentity = new ClaimsIdentity(minimalClaims, "Test");
                var minimalPrincipal = new ClaimsPrincipal(minimalIdentity);
                var minimalTicket = new AuthenticationTicket(minimalPrincipal, "Test");
                return Task.FromResult(AuthenticateResult.Success(minimalTicket));
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, "testuser"),
                new(ClaimTypes.NameIdentifier, "test-user-id")
            };

            if (authMode == "admin")
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

[Collection("IntegrationTests")]
public class DashboardHubSeededDataTests : IClassFixture<DashboardHubSeededDataTests.SeededHubTestFactory>, IAsyncDisposable
{
    private readonly SeededHubTestFactory _factory;
    private readonly HubConnection _hubConnection;

    public DashboardHubSeededDataTests(SeededHubTestFactory factory)
    {
        _factory = factory;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/dashboard",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Headers.Add("X-Test-Auth", "admin");
                })
            .Build();
    }

    [Fact]
    public async Task RequestFullUpdate_WithSeededData_ReturnsCorrectAggregates()
    {
        await _hubConnection.StartAsync();

        var tcs = new TaskCompletionSource<DashboardDto>();
        _hubConnection.On<DashboardDto>("ReceiveDashboardUpdate", dto => tcs.TrySetResult(dto));

        await _hubConnection.InvokeAsync("RequestFullUpdate");

        var dto = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        dto.BotEnabled.Should().BeTrue();
        dto.OpenPositionCount.Should().Be(2);
        dto.OpeningPositionCount.Should().Be(1);
        dto.NeedsAttentionCount.Should().Be(2); // 1 EmergencyClosed + 1 Failed
        dto.TotalPnl.Should().Be(1.5m + 2.5m); // Sum of AccumulatedFunding
        dto.BestSpread.Should().Be(0.003m); // Max CurrentSpreadPerHour
    }

    public async ValueTask DisposeAsync()
    {
        await _hubConnection.DisposeAsync();
    }

    public class SeededHubTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"HubSeededTest_{Guid.NewGuid()}";
        private bool _seeded;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "not-used",
                    ["Seed:AdminPassword"] = "NotUsed-InMemoryDb"
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
                    options.UseInMemoryDatabase(_dbName));

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
                    .AddScheme<AuthenticationSchemeOptions, SeededTestAuthHandler>("Test", _ => { });
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            if (!_seeded)
            {
                SeedTestData(host.Services);
                _seeded = true;
            }
            return host;
        }

        private static void SeedTestData(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();

            var botConfig = new BotConfiguration
            {
                IsEnabled = true,
                UpdatedByUserId = "test-user-id"
            };
            db.BotConfigurations.Add(botConfig);

            var asset = new Asset { Symbol = "BTC", Name = "Bitcoin" };
            db.Assets.Add(asset);

            var exchange1 = new Exchange
            {
                Name = "TestExch1",
                ApiBaseUrl = "https://test1.example.com",
                WsBaseUrl = "wss://test1.example.com"
            };
            var exchange2 = new Exchange
            {
                Name = "TestExch2",
                ApiBaseUrl = "https://test2.example.com",
                WsBaseUrl = "wss://test2.example.com"
            };
            db.Exchanges.AddRange(exchange1, exchange2);
            db.SaveChanges(); // Generate IDs

            // 2 Open positions with known funding and spread
            db.ArbitragePositions.Add(new ArbitragePosition
            {
                UserId = "test-user-id",
                AssetId = asset.Id,
                LongExchangeId = exchange1.Id,
                ShortExchangeId = exchange2.Id,
                SizeUsdc = 100m,
                Status = PositionStatus.Open,
                AccumulatedFunding = 1.5m,
                CurrentSpreadPerHour = 0.002m
            });
            db.ArbitragePositions.Add(new ArbitragePosition
            {
                UserId = "test-user-id",
                AssetId = asset.Id,
                LongExchangeId = exchange2.Id,
                ShortExchangeId = exchange1.Id,
                SizeUsdc = 200m,
                Status = PositionStatus.Open,
                AccumulatedFunding = 2.5m,
                CurrentSpreadPerHour = 0.003m
            });

            // 1 Opening position
            db.ArbitragePositions.Add(new ArbitragePosition
            {
                UserId = "test-user-id",
                AssetId = asset.Id,
                LongExchangeId = exchange1.Id,
                ShortExchangeId = exchange2.Id,
                SizeUsdc = 50m,
                Status = PositionStatus.Opening
            });

            // 1 EmergencyClosed + 1 Failed (needs attention)
            db.ArbitragePositions.Add(new ArbitragePosition
            {
                UserId = "test-user-id",
                AssetId = asset.Id,
                LongExchangeId = exchange1.Id,
                ShortExchangeId = exchange2.Id,
                SizeUsdc = 75m,
                Status = PositionStatus.EmergencyClosed
            });
            db.ArbitragePositions.Add(new ArbitragePosition
            {
                UserId = "test-user-id",
                AssetId = asset.Id,
                LongExchangeId = exchange2.Id,
                ShortExchangeId = exchange1.Id,
                SizeUsdc = 60m,
                Status = PositionStatus.Failed
            });

            db.SaveChanges();
        }
    }

    private sealed class SeededTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public SeededTestAuthHandler(
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
