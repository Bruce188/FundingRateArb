using System.Net;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Services;

public class DydxConnectorFactoryTests
{
    // Known valid BIP39 test fixture — deterministic, safe for tests.
    private const string ValidMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ResiliencePipelineProvider<string> BuildEmptyPipelineProvider()
    {
        var mock = new Mock<ResiliencePipelineProvider<string>>();
        mock.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);
        return mock.Object;
    }

    private static Mock<ILogger<DydxConnectorFactory>> BuildLoggerMock() =>
        new(MockBehavior.Loose);

    private static Mock<ILoggerFactory> BuildLoggerFactoryMock()
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
        return factory;
    }

    private static DydxConnectorFactory BuildFactory(
        HttpMessageHandler? validatorHandler = null,
        Mock<ILogger<DydxConnectorFactory>>? loggerMock = null)
    {
        var handler = validatorHandler ?? new StaticResponseHandler(HttpStatusCode.OK, "{}");
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient("DydxValidator"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://dydx-rpc.publicnode.com/") });
        httpFactory.Setup(f => f.CreateClient("DydxIndexer"))
            .Returns(new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, "{}"))
            { BaseAddress = new Uri("https://indexer.dydx.trade/v4/") });

        var logger = loggerMock?.Object ?? BuildLoggerMock().Object;
        var loggerFactory = BuildLoggerFactoryMock();

        return new DydxConnectorFactory(
            httpFactory.Object,
            BuildEmptyPipelineProvider(),
            logger,
            loggerFactory.Object,
            new SingletonMarkPriceCache());
    }

    // ── Validate ───────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NullMnemonic_ReturnsMissingMnemonic()
    {
        var sut = BuildFactory();
        var result = sut.Validate(null, null);

        result.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic);
        result.MissingField.Should().Be("Mnemonic");
        result.MnemonicPresent.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhitespaceMnemonic_ReturnsMissingMnemonic(string mnemonic)
    {
        var sut = BuildFactory();
        var result = sut.Validate(mnemonic, null);

        result.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic);
        result.MissingField.Should().Be("Mnemonic");
    }

    [Fact]
    public void Validate_InvalidBip39_ReturnsInvalidMnemonic()
    {
        var sut = BuildFactory();
        var result = sut.Validate("this is not a valid bip39 mnemonic phrase at all!", null);

        result.Reason.Should().Be(DydxCredentialFailureReason.InvalidMnemonic);
        result.MissingField.Should().Be("Mnemonic");
        result.MnemonicPresent.Should().BeTrue();
        result.MnemonicValidBip39.Should().BeFalse();
    }

    [Fact]
    public void Validate_ValidMnemonic_ReturnsNone_AllFlagsTrue()
    {
        var sut = BuildFactory();
        var result = sut.Validate(ValidMnemonic, null);

        result.Reason.Should().Be(DydxCredentialFailureReason.None);
        result.MnemonicPresent.Should().BeTrue();
        result.MnemonicValidBip39.Should().BeTrue();
        result.DerivedAddressValid.Should().BeTrue();
    }

    // ── ValidateSignedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ValidateSignedAsync_HandlerReturns200_ReturnsNone_IndexerReachableTrue()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, "{}");
        var sut = BuildFactory(handler);

        var result = await sut.ValidateSignedAsync(ValidMnemonic, null, CancellationToken.None);

        result.Reason.Should().Be(DydxCredentialFailureReason.None);
        result.IndexerReachable.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ValidateSignedAsync_HandlerReturnsNon200_ReturnsDerivedAddressInvalid(
        HttpStatusCode statusCode)
    {
        var handler = new StaticResponseHandler(statusCode, "{}");
        var sut = BuildFactory(handler);

        var result = await sut.ValidateSignedAsync(ValidMnemonic, null, CancellationToken.None);

        result.Reason.Should().Be(DydxCredentialFailureReason.DerivedAddressInvalid);
        result.IndexerReachable.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateSignedAsync_HandlerThrowsHttpRequestException_ReturnsIndexerUnreachable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("network error"));
        var sut = BuildFactory(handler);

        var result = await sut.ValidateSignedAsync(ValidMnemonic, null, CancellationToken.None);

        result.Reason.Should().Be(DydxCredentialFailureReason.IndexerUnreachable);
        result.IndexerReachable.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateSignedAsync_NullMnemonic_ReturnsMissingMnemonicWithoutHttpCall()
    {
        // Ensure the sync validation short-circuits before any HTTP call.
        var handler = new CountingStaticResponseHandler(HttpStatusCode.OK, "{}");
        var sut = BuildFactory(handler);

        var result = await sut.ValidateSignedAsync(null, null, CancellationToken.None);

        result.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic);
        handler.CallCount.Should().Be(0, "sync validation must short-circuit before HTTP");
    }

    // ── TryCreate ─────────────────────────────────────────────────────────

    [Fact]
    public void TryCreate_InvalidMnemonic_ReturnsFalse_NullConnector()
    {
        var sut = BuildFactory();
        var returned = sut.TryCreate("bad mnemonic", null, out var connector, out var result);

        returned.Should().BeFalse();
        connector.Should().BeNull();
        result.Reason.Should().NotBe(DydxCredentialFailureReason.None);
    }

    [Fact]
    public void TryCreate_ValidMnemonic_ReturnsTrue_NonNullConnector()
    {
        var sut = BuildFactory();
        var returned = sut.TryCreate(ValidMnemonic, null, out var connector, out var result);

        returned.Should().BeTrue();
        connector.Should().NotBeNull();
        result.Reason.Should().Be(DydxCredentialFailureReason.None);
    }

    // ── Logger leakage ────────────────────────────────────────────────────

    [Fact]
    public async Task Logger_NeverContainsMnemonicOrAddress()
    {
        // Derive the address from the fixture mnemonic so we can assert it's never logged.
        using var signer = new DydxSigner(ValidMnemonic);
        var fixtureAddress = signer.Address;

        var loggerMock = BuildLoggerMock();
        var capturedMessages = new List<string>();

        loggerMock
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => CaptureLog(capturedMessages, state.ToString()!)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Verifiable();

        // Trigger a signer-construction failure path to exercise LogWarning calls.
        var handler = new ThrowingHandler(new HttpRequestException("network error"));
        var sut = BuildFactory(handler, loggerMock);

        // Exercise ValidateSignedAsync with a valid mnemonic (reaches the HTTP call path).
        await sut.ValidateSignedAsync(ValidMnemonic, null, CancellationToken.None);

        // Exercise TryCreate
        sut.TryCreate(ValidMnemonic, null, out _, out _);

        foreach (var msg in capturedMessages)
        {
            msg.Should().NotContain(ValidMnemonic,
                "mnemonic must never appear in log output");
            msg.Should().NotContain(fixtureAddress,
                "derived bech32 address must never appear in log output");
        }
    }

    private static bool CaptureLog(List<string> list, string message)
    {
        list.Add(message);
        return true;
    }

    // ── HTTP handler helpers ───────────────────────────────────────────────

    private sealed class StaticResponseHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class CountingStaticResponseHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
