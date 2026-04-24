// review-v230: NB4 — ValidateDydxAsync does not emit log entries on failure or exception branches.
// review-v237: NB-3 — added System.Collections.Concurrent for ConcurrentQueue<string> in CapturingLogger<T>.
// Logging of validation outcomes is the caller's (ConnectorStartupValidator's) responsibility.
// This "no-log contract" is documented in the XML-doc remarks on ValidateDydxAsync in production.
// The capturedLogMessages.Should().BeEmpty(...) assertions in this file pin that invariant;
// a future maintainer adding a LogDebug trace to ValidateDydxAsync will see these tests fail,
// which is intentional — it signals that the contract is being changed, not silently broken.
using System.Collections.Concurrent;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.ExchangeConnectors;

/// <summary>
/// Unit tests for the dYdX orchestration surface of <see cref="ExchangeConnectorFactory"/>:
/// <see cref="ExchangeConnectorFactory.ValidateDydxAsync"/> and the
/// <see cref="ExchangeConnectorFactory.TryGetLastDydxFailure"/> cache.
/// </summary>
public class ExchangeConnectorFactoryDydxTests : IAsyncDisposable
{
    private const string SentinelMnemonic = "SENTINEL-MNEMONIC-WORDS-AAA-BBB"; // NOT A SECRET — leak-canary for log-hygiene tests (review-v236: N6)
    private const string UserId = "test-user-123";

    private ServiceProvider? _serviceProvider;

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="ExchangeConnectorFactory"/> with the supplied mocks wired
    /// into the DI service provider that the factory uses for scoped service resolution.
    /// </summary>
    /// <param name="userSettings">User settings mock (required).</param>
    /// <param name="dydxFactoryResult">
    /// If provided, configures the default <c>ValidateSignedAsync</c> return value on a fresh
    /// <see cref="IDydxConnectorFactory"/> mock. Mutually exclusive with <paramref name="configure"/>.
    /// </param>
    /// <param name="configure">
    /// review-v230: NB9 — optional configurator for advanced mock setup (e.g., SetupSequence).
    /// Called after the mock is constructed; use this to express any setup that cannot be
    /// expressed via a single <paramref name="dydxFactoryResult"/>. When this is supplied,
    /// <paramref name="dydxFactoryResult"/> must be <c>null</c> (review-v236: N4 — enforced).
    /// </param>
    /// <param name="logger">
    /// review-v236: NB-2 — optional logger; defaults to <see cref="NullLogger{T}.Instance"/>.
    /// Sentinel-log tests pass a <see cref="CapturingLogger{T}"/> here to remain on the single
    /// DI construction path rather than constructing a second inline <see cref="ServiceProvider"/>.
    /// </param>
    private (ExchangeConnectorFactory Factory, Mock<IDydxConnectorFactory> DydxFactory)
        BuildSut(
            Mock<IUserSettingsService> userSettings,
            DydxCredentialCheckResult? dydxFactoryResult = null,
            Action<Mock<IDydxConnectorFactory>>? configure = null,
            ILogger<ExchangeConnectorFactory>? logger = null)
    {
        // review-v236: NB-1 — guard against double-construction within the same test instance.
        if (_serviceProvider is not null)
        {
            throw new InvalidOperationException("BuildSut called twice in the same test instance");
        }

        // review-v236: N4 — enforce documented mutual exclusivity between dydxFactoryResult and configure.
        if (dydxFactoryResult is not null && configure is not null)
        {
            throw new ArgumentException(
                "dydxFactoryResult and configure are mutually exclusive — use configure for advanced setups");
        }

        var dydxFactory = new Mock<IDydxConnectorFactory>();

        if (dydxFactoryResult is not null)
        {
            dydxFactory
                .Setup(f => f.ValidateSignedAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(dydxFactoryResult);
        }

        // review-v230: NB9 — allow advanced mock configuration (e.g. SetupSequence) via
        // the configure delegate so all tests use a single DI construction path.
        configure?.Invoke(dydxFactory);

        var services = new ServiceCollection();
        services.AddSingleton(userSettings.Object);
        _serviceProvider = services.BuildServiceProvider();

        var factory = new ExchangeConnectorFactory(
            _serviceProvider,
            logger ?? NullLogger<ExchangeConnectorFactory>.Instance,
            dydxFactory.Object);

        return (factory, dydxFactory);
    }

    /// <summary>
    /// Creates a <see cref="UserExchangeCredential"/> with an Exchange whose name is
    /// <paramref name="exchangeName"/> (simulates a stored, active credential).
    /// </summary>
    private static UserExchangeCredential MakeCredential(string exchangeName) =>
        new()
        {
            UserId = UserId,
            Exchange = new Exchange { Name = exchangeName },
            EncryptedPrivateKey = "dummy-enc-key-for-test",
            EncryptedSubAccountAddress = string.Empty,  // review-v230: nit4 — removed stale "nit4:" token
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateDydxAsync_MissingMnemonic_ReturnsMissingMnemonicWithoutCallingFactory()
    {
        // Arrange — no dYdX credential in the user's active credentials
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync([]); // review-v240: N2 — collection expression (empty)

        var (sut, dydxFactory) = BuildSut(userSettings);

        // Act
        var result = await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — short-circuit: MissingMnemonic without touching IDydxConnectorFactory
        result.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic);
        result.MissingField.Should().Be("Mnemonic");
        dydxFactory.Verify(
            f => f.ValidateSignedAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);  // ValidateSignedAsync must NOT be called when the credential is absent

        // Assert — the MissingMnemonic short-circuit also writes to the failure cache
        sut.TryGetLastDydxFailure(UserId, out var cached).Should().BeTrue(
            "MissingMnemonic short-circuit must write the failure result to the cache");
        cached.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic);
    }

    // review-v230: NB6 — NEW test for the non-empty credentials, no-dYdX-entry path.
    // Production triggers MissingMnemonic on TWO paths: (1) empty credential list and
    // (2) non-empty list where no entry matches Exchange.Name == "dydx" (OrdinalIgnoreCase).
    // The test above covers path 1; this test covers path 2.
    [Fact]
    public async Task ValidateDydxAsync_NonEmptyCredentialsNoDydx_ReturnsMissingMnemonicWithoutCallingFactory()
    {
        // Arrange — credential list is non-empty but no entry matches "dydx" OrdinalIgnoreCase
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[MakeCredential("Binance")]); // review-v240: N2

        var (sut, dydxFactory) = BuildSut(userSettings);

        // Act
        var result = await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — same short-circuit as empty-list path: MissingMnemonic, factory not called
        result.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic,
            "non-empty credential list with no dYdX entry must short-circuit to MissingMnemonic");
        result.MissingField.Should().Be("Mnemonic");
        dydxFactory.Verify(
            f => f.ValidateSignedAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "factory must NOT be called when no dYdX credential is found in a non-empty list");

        // review-v236: N3 — assert the failure was written to the cache (consistent with the
        // empty-list path which also asserts the cache write after MissingMnemonic short-circuit).
        sut.TryGetLastDydxFailure(UserId, out var cached).Should().BeTrue(
            "MissingMnemonic short-circuit must write the failure result to the cache");
        cached.Reason.Should().Be(DydxCredentialFailureReason.MissingMnemonic,
            "cached result must reflect the MissingMnemonic short-circuit");
    }

    // review-v230: NB5 — NEW test for the never-validated-user cache-miss path.
    // TryGetLastDydxFailure is tested after failure and after success, but not for a user
    // who was never passed to ValidateDydxAsync. A regression that pre-populated the cache
    // with default entries for unknown users would be undetected without this test.
    [Fact]
    public void ValidateDydxAsync_NeverValidatedUser_TryGetLastDydxFailureReturnsFalse()
    {
        // Arrange — construct SUT with no prior ValidateDydxAsync calls
        var userSettings = new Mock<IUserSettingsService>();
        var (sut, _) = BuildSut(userSettings);

        // Act — no prior ValidateDydxAsync invocation for "unknown-user-id"
        var returned = sut.TryGetLastDydxFailure("unknown-user-id", out var miss);

        // Assert — cache miss: returns false for an unknown user
        returned.Should().BeFalse(
            "TryGetLastDydxFailure must return false for a user ID never passed to ValidateDydxAsync");
        // review-v236: N2 — assert the discarded out-param is the default struct value
        miss.Should().Be(default(DydxCredentialCheckResult),
            "the out-param must be the default struct when TryGetLastDydxFailure returns false");
    }

    [Fact]
    public async Task ValidateDydxAsync_Failure_WritesEntryToLastDydxFailuresCache()
    {
        // Arrange — credential present; downstream factory returns a failure
        var cred = MakeCredential("dYdX");
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[cred]); // review-v240: N2
        userSettings
            .Setup(u => u.DecryptCredential(cred))
            .Returns((null, null, null, "decrypted-mnemonic", null, null));

        var failureResult = new DydxCredentialCheckResult
        {
            Reason = DydxCredentialFailureReason.InvalidMnemonic,
            MissingField = "Mnemonic"
        };
        var (sut, _) = BuildSut(userSettings, failureResult);

        // Act
        await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — failure stored in cache, readable via TryGetLastDydxFailure
        sut.TryGetLastDydxFailure(UserId, out var cached).Should().BeTrue();
        cached.Reason.Should().Be(DydxCredentialFailureReason.InvalidMnemonic);
    }

    [Fact]
    public async Task ValidateDydxAsync_CredentialPresent_CallsDecryptCredential()
    {
        // Arrange
        var cred = MakeCredential("dydx"); // lower-case matches OrdinalIgnoreCase lookup
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[cred]); // review-v240: N2

        // Return a known tuple from DecryptCredential to confirm the right credential is passed
        var expectedPrivateKey = "decrypted-mnemonic-key";
        userSettings
            .Setup(u => u.DecryptCredential(cred))
            .Returns((null, null, null, expectedPrivateKey, null, null));

        var successResult = new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None };
        var (sut, dydxFactory) = BuildSut(userSettings, successResult);

        // Act
        await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — DecryptCredential was called with the exact credential object
        userSettings.Verify(u => u.DecryptCredential(cred), Times.Once);
        // The decrypted private key was forwarded to ValidateSignedAsync
        dydxFactory.Verify(
            f => f.ValidateSignedAsync(expectedPrivateKey, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ValidateDydxAsync_ExchangeNameLookup_IsCaseInsensitive()
    {
        // Arrange — credential stored with exchange name "DYDX" (all caps)
        // The production lookup uses StringComparison.OrdinalIgnoreCase, so it SHOULD match.
        var cred = MakeCredential("DYDX");
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[cred]); // review-v240: N2
        userSettings
            .Setup(u => u.DecryptCredential(cred))
            .Returns((null, null, null, "some-key", null, null));

        var successResult = new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None };
        var (sut, dydxFactory) = BuildSut(userSettings, successResult);

        // Act
        var result = await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — OBSERVED BEHAVIOUR: OrdinalIgnoreCase matches "DYDX" == "dydx".
        // If this assertion fails it means the lookup semantics changed — update accordingly.
        result.Reason.Should().Be(DydxCredentialFailureReason.None,
            because: "the OrdinalIgnoreCase comparison matches 'DYDX' to 'dydx'");
        dydxFactory.Verify(
            f => f.ValidateSignedAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);  // ValidateSignedAsync must be called when the credential is found case-insensitively
    }

    // review-v230: NB9 — migrated to use BuildSut(configure:) so all tests share a single
    // DI construction path; removed the inline ServiceCollection/ServiceProvider setup.
    [Fact]
    public async Task ValidateDydxAsync_AfterFailureThenSuccess_CacheReflectsSuccessResult()
    {
        // Arrange — first call fails; second call succeeds
        var cred = MakeCredential("dYdX");
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[cred]); // review-v240: N2
        userSettings
            .Setup(u => u.DecryptCredential(cred))
            .Returns((null, null, null, "mnemonic-key", null, null));

        var failureResult = new DydxCredentialCheckResult
        {
            Reason = DydxCredentialFailureReason.InvalidMnemonic,
            MissingField = "Mnemonic"
        };
        var successResult = new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None };

        // review-v230: NB9 — SetupSequence expressed through BuildSut(configure:) instead
        // of a separate inline DI construction path.
        var (sut, _) = BuildSut(
            userSettings,
            configure: m => m
                .SetupSequence(f => f.ValidateSignedAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(failureResult)
                .ReturnsAsync(successResult));

        // Act — first call produces a failure
        await sut.ValidateDydxAsync(UserId, CancellationToken.None);
        sut.TryGetLastDydxFailure(UserId, out var afterFailure).Should().BeTrue();
        afterFailure.Reason.Should().Be(DydxCredentialFailureReason.InvalidMnemonic);

        // Act — second call succeeds; cache should be updated
        await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — OBSERVED BEHAVIOUR: the success result OVERWRITES the cache entry
        // (it does not remove the key). TryGetLastDydxFailure returns true but Reason == None.
        sut.TryGetLastDydxFailure(UserId, out var afterSuccess).Should().BeTrue(
            because: "the cache key is overwritten, not removed, on successful validation");
        afterSuccess.Reason.Should().Be(DydxCredentialFailureReason.None,
            because: "the success result was written to the cache, clearing the prior failure");
    }

    [Fact]
    public async Task ValidateDydxAsync_SentinelMnemonicInFailureBranch_FactoryEmitsNoLogEntries()
    {
        // Arrange — sentinel mnemonic is the decrypted private key; downstream factory
        // returns a failure result with a safe field name.
        //
        // review-v230: NB4 — The failure branch of ValidateDydxAsync stores the result in
        // _lastDydxFailures and returns — it does NOT emit any log entries.
        // Asserting BeEmpty() pins this "no-log contract" invariant; a regression that added
        // a warning/error log on the failure path (potentially including credential material)
        // would be caught immediately rather than requiring a separate sentinel-leak scan.
        // The contract is documented in an XML-doc <remarks> on ValidateDydxAsync in production.
        //
        // review-v236: NB-2 — migrated from inline DI construction to BuildSut(logger:) to
        // eliminate the second ServiceProvider construction path (NB9 compliance).
        var cred = MakeCredential("dYdX");
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[cred]); // review-v240: N2
        userSettings
            .Setup(u => u.DecryptCredential(cred))
            .Returns((null, null, null, SentinelMnemonic, null, null));

        // review-v237: NB-3 — ConcurrentQueue<string> matches the pattern used in ConnectorStartupValidatorTests;
        // the factory is always invoked synchronously from the test thread today, but ConcurrentQueue
        // eliminates the latent risk if a future test exercises a concurrent logging path.
        var capturedLogMessages = new ConcurrentQueue<string>();
        var capturingLogger = new CapturingLogger<ExchangeConnectorFactory>(capturedLogMessages);

        var failureResult = new DydxCredentialCheckResult
        {
            Reason = DydxCredentialFailureReason.InvalidMnemonic,
            MissingField = "Mnemonic"
        };

        // review-v236: NB-2 — use BuildSut(configure:, logger:) to stay on the single DI path.
        // configure sets up ValidateSignedAsync for the sentinel key; logger captures any emission.
        var (sut, _) = BuildSut(
            userSettings,
            configure: m => m
                .Setup(f => f.ValidateSignedAsync(
                    SentinelMnemonic,
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(failureResult),
            logger: capturingLogger);

        // Act
        await sut.ValidateDydxAsync(UserId, CancellationToken.None);

        // Assert — the factory must not emit any log messages on the failure branch (NB4).
        // Logging of the validation outcome is the caller's (ConnectorStartupValidator's) responsibility.
        capturedLogMessages.Should().BeEmpty(
            "ValidateDydxAsync does not log on the failure branch — logging is delegated to the caller; " +
            "any log entry here risks emitting credential material or misleading diagnostics");
    }

    [Fact]
    public async Task ValidateDydxAsync_SentinelMnemonicInExceptionBranch_FactoryEmitsNoLogEntries()
    {
        // Arrange — downstream factory throws an exception whose message contains the sentinel mnemonic.
        // ValidateDydxAsync propagates the exception to the caller without catching or logging it.
        //
        // review-v230: NB4 — The exception branch is a "no-log contract" — the factory propagates the
        // exception raw, making capturedLogMessages.BeEmpty() a precise invariant.
        // If the factory ever added a catch/log before re-throwing, this test would fail,
        // alerting the developer that credential material from the exception message could leak.
        // The contract is documented in an XML-doc <remarks> on ValidateDydxAsync in production.
        //
        // review-v236: NB-2 — migrated from inline DI construction to BuildSut(configure:, logger:)
        // to eliminate the second ServiceProvider construction path (NB9 compliance).
        var cred = MakeCredential("dYdX");
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetActiveCredentialsAsync(UserId))
            .ReturnsAsync((List<UserExchangeCredential>)[cred]); // review-v240: N2
        userSettings
            .Setup(u => u.DecryptCredential(cred))
            .Returns((null, null, null, SentinelMnemonic, null, null));

        // review-v237: NB-3 — ConcurrentQueue<string> for symmetry with ConnectorStartupValidatorTests.
        var capturedLogMessages = new ConcurrentQueue<string>();
        var capturingLogger = new CapturingLogger<ExchangeConnectorFactory>(capturedLogMessages);

        // review-v236: NB-2 — BuildSut(configure:, logger:) stays on the single DI path.
        var (sut, _) = BuildSut(
            userSettings,
            configure: m => m
                .Setup(f => f.ValidateSignedAsync(
                    SentinelMnemonic,
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException($"network error (not credential: {SentinelMnemonic})")),
            logger: capturingLogger);

        // Act — the factory propagates the exception without catching or logging it
        var act = async () => await sut.ValidateDydxAsync(UserId, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert — no log entries emitted (NB4 "no-log contract").
        // review-v239: NB-3 — this assertion is trivially true given the current implementation:
        // ValidateDydxAsync has no try/catch, so there is no code path that could emit a log
        // entry before the exception propagates. Its purpose is to fail fast if a future maintainer
        // adds a catch/log before re-throw — which would risk emitting credential material from
        // ex.Message into the log stream. Keep as a forward-looking contract pin, not a live assertion.
        // The caller (ConnectorStartupValidator) is responsible for catching and logging
        // iteration-level exceptions.
        capturedLogMessages.Should().BeEmpty(
            "ValidateDydxAsync propagates exceptions without logging — any catch/log here risks " +
            "emitting credential material from the exception message");
    }

    // ── Test double ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures formatted log message strings for assertion in no-log-contract tests.
    /// review-v237: NB-3 — uses <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> for
    /// thread-safety symmetry with the equivalent logger in ConnectorStartupValidatorTests.
    /// The factory is currently invoked synchronously from the test thread, so no corruption
    /// is observable today; ConcurrentQueue eliminates the latent risk for future async paths.
    /// </summary>
    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages;

        public CapturingLogger(ConcurrentQueue<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
        }
    }
}
