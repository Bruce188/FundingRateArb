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

namespace FundingRateArb.Tests.Integration.Areas.Admin;

/// <summary>
/// Integration tests for GET /Admin/Credentials/DydxValidate.
/// Verifies authorization, 404 handling, per-field validation, and
/// that the response body never contains mnemonic or derived address values.
/// </summary>
[Collection("IntegrationTests")]
public class CredentialsControllerTests : IClassFixture<CredentialsControllerTests.CredentialsTestFactory>, IDisposable
{
    private const string ValidMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private readonly CredentialsTestFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _anonClient;

    public CredentialsControllerTests(CredentialsTestFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("X-Test-Auth", "Admin");

        _anonClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _adminClient.Dispose();
        _anonClient.Dispose();
    }

    [Fact]
    public async Task DydxValidate_Unauthenticated_RedirectsToLogin()
    {
        var response = await _anonClient.GetAsync("/Admin/Credentials/DydxValidate?userId=someuser");

        // Non-authenticated requests to Admin area redirect to login (302) or return 401
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DydxValidate_NonAdmin_ReturnsForbiddenOrRedirect()
    {
        var userClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        userClient.DefaultRequestHeaders.Add("X-Test-Auth", "User");

        var response = await userClient.GetAsync("/Admin/Credentials/DydxValidate?userId=someuser");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.NotFound);
        userClient.Dispose();
    }

    [Fact]
    public async Task DydxValidate_MissingUserId_ReturnsBadRequest()
    {
        var response = await _adminClient.GetAsync("/Admin/Credentials/DydxValidate");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DydxValidate_AdminAndMissingUser_Returns404()
    {
        _factory.ConnectorFactory.ValidateDydxResult = _ =>
            throw new KeyNotFoundException("User not found");

        var response = await _adminClient.GetAsync("/Admin/Credentials/DydxValidate?userId=nonexistent-user");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DydxValidate_AdminAndInvalidMnemonic_Returns200_BodyHasInvalidMnemonicReason()
    {
        _factory.ConnectorFactory.ValidateDydxResult = _ => new DydxCredentialCheckResult
        {
            MnemonicPresent = true,
            Reason = DydxCredentialFailureReason.InvalidMnemonic,
            MissingField = "Mnemonic"
        };

        var response = await _adminClient.GetAsync("/Admin/Credentials/DydxValidate?userId=baduser");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // Enum is serialized as integer by default; InvalidMnemonic = 2
        body.Should().MatchRegex("\"reason\"\\s*:\\s*2",
            "response must include the InvalidMnemonic reason (value=2) for an invalid mnemonic");
    }

    [Fact]
    public async Task DydxValidate_AdminAndValidCreds_Returns200_BodyHasNoneReason()
    {
        _factory.ConnectorFactory.ValidateDydxResult = _ => new DydxCredentialCheckResult
        {
            MnemonicPresent = true,
            MnemonicValidBip39 = true,
            SubAccountPresent = false,
            DerivedAddressValid = true,
            IndexerReachable = true,
            Reason = DydxCredentialFailureReason.None
        };

        var response = await _adminClient.GetAsync("/Admin/Credentials/DydxValidate?userId=validuser");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // Enum is serialized as integer by default; None = 0
        body.Should().MatchRegex("\"reason\"\\s*:\\s*0",
            "response body must contain reason=0 (None) for valid credentials");
    }

    [Fact]
    public async Task DydxValidate_ResponseBody_NeverContainsMnemonicOrAddress()
    {
        // Derive the bech32 address from the fixture mnemonic
        using var signer = new FundingRateArb.Infrastructure.ExchangeConnectors.Dydx.DydxSigner(ValidMnemonic);
        var fixtureAddress = signer.Address;

        _factory.ConnectorFactory.ValidateDydxResult = _ => new DydxCredentialCheckResult
        {
            MnemonicPresent = true,
            MnemonicValidBip39 = true,
            DerivedAddressValid = true,
            IndexerReachable = true,
            Reason = DydxCredentialFailureReason.None
        };

        var response = await _adminClient.GetAsync("/Admin/Credentials/DydxValidate?userId=leaktest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(ValidMnemonic,
            "mnemonic must never appear in the response body");
        body.Should().NotContain(fixtureAddress,
            "derived bech32 address must never appear in the response body");
    }

    // ── Test infrastructure ────────────────────────────────────────────────

    public class CredentialsTestFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = $"CredentialsTest_{Guid.NewGuid()}";
        internal readonly StubExchangeConnectorFactory ConnectorFactory = new();

        public void SetupMockFactory(Action<StubExchangeConnectorFactory> configure)
        {
            configure(ConnectorFactory);
        }

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
                // Replace EF with InMemory
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
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(DbName));

                // Remove real stream registrations
                var streamDescriptors = services.Where(d => d.ServiceType == typeof(IMarketDataStream)).ToList();
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
                var botControlDescriptors = services.Where(d => d.ServiceType == typeof(IBotControl)).ToList();
                foreach (var d in botControlDescriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton<IBotControl>(new StubBotControl());

                // Replace IExchangeConnectorFactory with the stub
                var factoryDescriptors = services
                    .Where(d => d.ServiceType == typeof(IExchangeConnectorFactory))
                    .ToList();
                foreach (var d in factoryDescriptors)
                {
                    services.Remove(d);
                }
                services.AddScoped<IExchangeConnectorFactory>(_ => ConnectorFactory);

                // Add test authentication
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }
    }

    /// <summary>
    /// Configurable stub for IExchangeConnectorFactory used in controller integration tests.
    /// </summary>
    public sealed class StubExchangeConnectorFactory : IExchangeConnectorFactory
    {
        /// <summary>
        /// Set this to control what ValidateDydxAsync returns (or throws).
        /// If null, throws <see cref="KeyNotFoundException"/> by default.
        /// </summary>
        public Func<string, DydxCredentialCheckResult>? ValidateDydxResult { get; set; }

        public Task<DydxCredentialCheckResult> ValidateDydxAsync(string userId, CancellationToken ct)
        {
            if (ValidateDydxResult is null)
            {
                throw new KeyNotFoundException($"No result configured for userId '{userId}'");
            }

            try
            {
                return Task.FromResult(ValidateDydxResult(userId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Task.FromException<DydxCredentialCheckResult>(ex);
            }
        }

        public bool TryGetLastDydxFailure(string userId, out DydxCredentialCheckResult result)
        {
            result = default!;
            return false;
        }

        public IExchangeConnector GetConnector(string exchangeName) =>
            throw new NotImplementedException();

        public IEnumerable<IExchangeConnector> GetAllConnectors() =>
            throw new NotImplementedException();

        public Task<IExchangeConnector?> CreateForUserAsync(
            string exchangeName, string? apiKey, string? apiSecret,
            string? walletAddress, string? privateKey,
            string? subAccountAddress, string? apiKeyIndex, string? userId) =>
            Task.FromResult<IExchangeConnector?>(null);
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
                new(ClaimTypes.Name, "testuser"),
                new(ClaimTypes.NameIdentifier, "test-user-id"),
            };

            // Add Admin role only when explicitly requested
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
