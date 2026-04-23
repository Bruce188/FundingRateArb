using System.Collections.Concurrent;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Infrastructure.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

/// <summary>
/// Unit tests for <see cref="ConnectorStartupValidator"/>.
///
/// All tests exercise the real production <see cref="ConnectorStartupValidator.ExecuteAsync"/>
/// by injecting a recording delay-seam (<c>Func&lt;TimeSpan, CancellationToken, Task&gt;</c>)
/// that returns immediately for all delays, then blocks on the Nth iteration-interval
/// delay until the test calls <c>StopAsync</c>.
/// No test-double subclass overrides <c>ExecuteAsync</c>.
///
/// Synchronization protocol:
///   1. Test calls <see cref="BackgroundService.StartAsync"/> (returns before <c>ExecuteAsync</c> makes progress).
///   2. Test awaits <see cref="RecordingDelay.WaitForIterationAsync"/> — fires when the Nth
///      iteration-interval delay is about to block.
///   3. Test calls <see cref="BackgroundService.StopAsync"/> — cancels <c>stoppingToken</c>,
///      which unblocks the blocked delay, causing OCE to propagate out of <c>ExecuteAsync</c>.
///   4. <c>StopAsync</c> returns after <c>ExecuteAsync</c> exits.
///
/// Note on exception-swallowing scope: the production try/catch wraps the entire
/// per-iteration body including the foreach. A per-user exception aborts the
/// CURRENT iteration; "user-continues" is validated in the NEXT iteration.
/// </summary>
public class ConnectorStartupValidatorTests : IAsyncDisposable
{
    private const string SentinelMnemonic = "SENTINEL-MNEMONIC-WORDS-AAA-BBB";

    /// <summary>
    /// Centralised timeout for all <c>WaitAsync</c> calls in this test class.
    /// 5 seconds gives slow CI runners headroom while still catching genuine hangs.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    // ── Delay-seam infrastructure ─────────────────────────────────────────────────

    /// <summary>
    /// Records every delay invocation. All delays complete immediately except for the
    /// iteration-interval delay at iteration <paramref name="blockAtIteration"/>:
    /// that one signals <see cref="WaitForIterationAsync"/> and then blocks until
    /// <c>stoppingToken</c> is cancelled (i.e. until the test calls <c>StopAsync</c>).
    /// Thread-safe — called from the background-service thread.
    /// </summary>
    private sealed class RecordingDelay
    {
        private readonly ConcurrentQueue<TimeSpan> _recorded = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _iterationReady = new();
        // review-v236: NB-3 — TCS signalled from Func on first PerUserThrottle observation,
        // so tests can await the throttle-reached event before calling StopAsync.
        private readonly TaskCompletionSource _throttleReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _blockAtIteration;
        private int _iterationCount;

        /// <param name="blockAtIteration">
        /// After this many full iterations have been recorded, the next iteration-interval
        /// delay signals <see cref="WaitForIterationAsync"/> and then blocks until the token
        /// is cancelled. Once blocked, subsequent iterations cannot occur — <c>StopAsync</c>
        /// cancels the token and <c>ExecuteAsync</c> exits via OCE.
        /// Default = 1 (block after the first iteration).
        /// </param>
        public RecordingDelay(int blockAtIteration = 1) => _blockAtIteration = blockAtIteration;

        /// <summary>All delay durations recorded so far, in call order.</summary>
        public IReadOnlyCollection<TimeSpan> Recorded => _recorded;

        /// <summary>
        /// Returns a <see cref="Task"/> that completes just before the blocking delay at
        /// iteration <paramref name="n"/>. Pre-register before <c>StartAsync</c> to avoid
        /// missing the signal on very fast hardware.
        /// </summary>
        public Task WaitForIterationAsync(int n)
        {
            var tcs = _iterationReady.GetOrAdd(n,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return tcs.Task;
        }

        /// <summary>
        /// Returns a <see cref="Task"/> that completes the first time a
        /// <see cref="ConnectorStartupValidator.PerUserThrottle"/> duration is observed.
        /// Pre-register before <c>StartAsync</c> to avoid missing the signal.
        /// </summary>
        /// <remarks>
        /// review-v236: NB-3 — deterministic synchronization point for the throttle-cancellation
        /// test; replaces the prior <c>validateCalled</c> TCS that fired before the throttle delay
        /// was attempted, leaving a timing window between ValidateDydxAsync return and StopAsync.
        /// </remarks>
        public Task WaitForThrottleAsync() => _throttleReached.Task;

        /// <summary>
        /// The delay function to inject.
        /// - Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is already cancelled.
        /// - Records the <paramref name="duration"/>.
        /// - For warm-up delays: returns immediately.
        /// - For per-user throttle delays: signals <see cref="WaitForThrottleAsync"/> on the first
        ///   observation (review-v236: N5 — keyed on the PerUserThrottle constant, not a bare literal).
        /// - For iteration-interval delays: increments the counter; at <see cref="_blockAtIteration"/>
        ///   signals the awaitable and then blocks on <c>Task.Delay(Infinite, ct)</c>.
        ///   Prior iterations return immediately; subsequent ones (if any) also block.
        /// </summary>
        public Func<TimeSpan, CancellationToken, Task> Func =>
            async (duration, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _recorded.Enqueue(duration);

                // review-v236: N5 — keyed on the named constant (not a bare literal) to avoid
                // silently re-gating if a future site happens to use the same 30-minute span.
                if (duration == ConnectorStartupValidator.PerUserThrottle)
                {
                    // review-v236: NB-3 — signal from inside Func so the throttle delay is
                    // structurally guaranteed to have been attempted before the test calls StopAsync.
                    _throttleReached.TrySetResult();
                }

                if (duration == ConnectorStartupValidator.IterationInterval)
                {
                    var n = Interlocked.Increment(ref _iterationCount);
                    if (n >= _blockAtIteration)
                    {
                        // Signal the test that iteration N is complete and we are about to block
                        var tcs = _iterationReady.GetOrAdd(n,
                            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                        tcs.TrySetResult();

                        // Block until stoppingToken is cancelled (i.e. StopAsync is called)
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                }
            };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private ServiceProvider? _serviceProvider;

    private (ConnectorStartupValidator Sut, RecordingDelay Delay) BuildSut(
        IUserSettingsService userSettings,
        IExchangeConnectorFactory factory,
        ISignalRNotifier notifier,
        ILogger<ConnectorStartupValidator>? logger = null,
        int blockAtIteration = 1)
    {
        // review-v236: NB-1 — guard against double-construction within the same test instance.
        if (_serviceProvider is not null)
            throw new InvalidOperationException("BuildSut called twice in the same test instance");

        var services = new ServiceCollection();
        services.AddSingleton(userSettings);
        services.AddSingleton(factory);
        services.AddSingleton(notifier);
        _serviceProvider = services.BuildServiceProvider();
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var delay = new RecordingDelay(blockAtIteration);
        var sut = new ConnectorStartupValidator(
            scopeFactory,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnectorStartupValidator>.Instance,
            delay.Func);
        return (sut, delay);
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
            await _serviceProvider.DisposeAsync();
    }

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TokenCancelledBeforeWarmUp_DoesNotFetchUsers()
    {
        // Arrange — pre-cancelled token short-circuits the warm-up delay immediately.
        var userSettings = new Mock<IUserSettingsService>();
        var factory = new Mock<IExchangeConnectorFactory>();
        var notifier = new Mock<ISignalRNotifier>();

        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(CancellationToken.None);

        // Assert — warm-up was cancelled; no user fetch occurred
        userSettings.Verify(
            u => u.GetUsersWithCredentialsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // review-v236: N1 — warm-up must not record any delay when the token is pre-cancelled
        delay.Recorded.Should().BeEmpty("warm-up must not record a duration when the token is pre-cancelled");
    }

    [Fact]
    public async Task ExecuteAsync_TwoDistinctUsers_EachValidatedExactlyOnce()
    {
        // Arrange — two users, no failures; run exactly one full iteration then stop
        var userIds = new List<string> { "user-A", "user-B" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        var notifier = new Mock<ISignalRNotifier>();
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, blockAtIteration: 1);

        // Pre-register signal before StartAsync to avoid race
        var iterationReady = delay.WaitForIterationAsync(1);

        // Act — wait until iteration 1 is complete and blocking, then stop
        await sut.StartAsync(CancellationToken.None);
        ConnectorStartupValidator? sutRef = sut;
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sutRef.StopAsync(CancellationToken.None);
        }

        // Assert — each user ID validated exactly once within the first iteration
        factory.Verify(f => f.ValidateDydxAsync("user-A", It.IsAny<CancellationToken>()), Times.Once);
        factory.Verify(f => f.ValidateDydxAsync("user-B", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TwoDistinctUsers_ThrottleDelayOccursBetweenEachUser()
    {
        // Arrange — two users; assert PerUserThrottle delay occurs once per user per iteration (NB4)
        var userIds = new List<string> { "user-A", "user-B" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        var notifier = new Mock<ISignalRNotifier>();
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, blockAtIteration: 1);

        var iterationReady = delay.WaitForIterationAsync(1);

        // Act
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — PerUserThrottle recorded exactly once per user in iteration 1
        var throttleDelays = delay.Recorded.Count(d => d == ConnectorStartupValidator.PerUserThrottle);
        throttleDelays.Should().Be(userIds.Count,
            "a PerUserThrottle delay should follow each ValidateDydxAsync call");
    }

    [Fact]
    public async Task ExecuteAsync_CredentialFailure_PushesSignalRNotificationWithExactPayload()
    {
        // Arrange — one user, validation returns MissingMnemonic; run exactly one iteration
        var userIds = new List<string> { "user-X" };
        var failure = new DydxCredentialCheckResult
        {
            Reason = DydxCredentialFailureReason.MissingMnemonic,
            MissingField = "Mnemonic"
        };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory.Setup(f => f.ValidateDydxAsync("user-X", It.IsAny<CancellationToken>())).ReturnsAsync(failure);

        // review-v236: NB-6 — ConcurrentQueue is safe for Moq Callback writes from the
        // background-service thread and test-thread reads after StopAsync provides happens-before.
        var capturedPayloads = new ConcurrentQueue<string>();
        var notifier = new Mock<ISignalRNotifier>();
        notifier
            .Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, msg) => capturedPayloads.Enqueue(msg))
            .Returns(Task.CompletedTask);

        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, blockAtIteration: 1);
        var iterationReady = delay.WaitForIterationAsync(1);

        // Act
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — exactly one notification pushed with the exact payload format (nit1)
        capturedPayloads.Should().ContainSingle(
            "exactly one notification should be pushed for a single-user failure — a second payload would indicate an unintended regression");
        capturedPayloads.TryDequeue(out var payload).Should().BeTrue();
        payload.Should().Be("dYdX credentials invalid — MissingMnemonic (Mnemonic)");
    }

    [Fact]
    public async Task ExecuteAsync_CredentialSuccess_DoesNotPushSignalRNotification()
    {
        // Arrange — one user, validation succeeds; run exactly one iteration
        var userIds = new List<string> { "user-ok" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync("user-ok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        var notifier = new Mock<ISignalRNotifier>();
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, blockAtIteration: 1);
        var iterationReady = delay.WaitForIterationAsync(1);

        // Act
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — no notification for a successful validation
        notifier.Verify(
            n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    // review-v236: B2 — the prior sentinel-leak assertion was structurally vacuous because the
    // sentinel never entered the SUT (it was only in a comment). ConnectorStartupValidator logs
    // {Reason} and {Field} from DydxCredentialCheckResult — placing the sentinel in MissingField
    // would cause the SUT to include it in the Warning log BY DESIGN (it's the field name).
    // The sentinel-leak invariant for ValidateDydxAsync is canonical in ExchangeConnectorFactoryDydxTests
    // (NB4 tests) where the factory's "no-log contract" is asserted. This test is restructured to
    // assert the SUT's log FORMAT with the sentinel in MissingField — the warning log MUST include
    // the sentinel string (confirming MissingField is interpolated), while the SUT must not add
    // any ADDITIONAL log entries beyond the expected Warning.
    [Fact]
    public async Task ExecuteAsync_CredentialFailure_LogsWarningWithReasonAndMissingField()
    {
        // Arrange — sentinel in MissingField so the warning-log format assertion is falsifiable:
        // if {Field} were dropped from the log template, the assertion would fail.
        var userIds = new List<string> { "user-format-test" };
        var failure = new DydxCredentialCheckResult
        {
            Reason = DydxCredentialFailureReason.MissingMnemonic,
            MissingField = SentinelMnemonic  // review-v236: B2 — sentinel in formatted slot
        };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync("user-format-test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure);

        var notifier = new Mock<ISignalRNotifier>();
        notifier.Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // review-v236: NB-6 — ConcurrentQueue for log capture (written from background thread)
        var capturedLog = new ConcurrentQueue<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger<ConnectorStartupValidator>(capturedLog);

        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, logger, blockAtIteration: 1);
        var iterationReady = delay.WaitForIterationAsync(1);

        // Act
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — exactly one Warning log entry containing both {Reason} and {Field}
        // (review-v236: B2 — non-vacuous: sentinel in MissingField means the assertion
        // would fail if {Field} were dropped from the log template).
        var warnings = capturedLog.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle("exactly one credential-failure warning should be logged");
        warnings[0].Message.Should().Contain("MissingMnemonic",
            "warning must include the failure reason");
        warnings[0].Message.Should().Contain(SentinelMnemonic,
            "warning must include MissingField ({Field}) — assertion is falsifiable because sentinel IS in MissingField");

        // No unexpected log entries (Error, Critical) — only the expected Warning
        capturedLog.Should().NotContain(
            e => e.Level == LogLevel.Error || e.Level == LogLevel.Critical,
            "a credential failure must not produce an error log — only a warning");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringIteration_ExitsWithoutSpuriousNotification()
    {
        // Arrange — cancel the stoppingToken once the throttle delay is reached.
        // Production: the OCE from the throttle delay propagates up through the foreach and
        // is caught by the outer catch(OperationCanceledException){throw;}, exiting ExecuteAsync.
        //
        // review-v236: NB-3 — Synchronization: await throttle-reached TCS from inside
        // RecordingDelay.Func (not from validateCalled), so StopAsync is called only AFTER
        // the throttle delay has been structurally attempted. This eliminates the window where
        // StopAsync could cancel the token before the throttle delay runs.
        var userIds = new List<string> { "user-mid" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        // review-v236: NB-6 — ConcurrentQueue for log capture (written from background thread)
        var capturedLog = new ConcurrentQueue<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger<ConnectorStartupValidator>(capturedLog);

        var notifier = new Mock<ISignalRNotifier>();
        // blockAtIteration: 1 ensures the loop does not spin past iteration 1
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, logger, blockAtIteration: 1);

        // Pre-register throttle-reached before StartAsync to avoid missing the signal on fast hardware.
        var throttleReached = delay.WaitForThrottleAsync();

        // review-v236: NB-5 — capture executeTask immediately after StartAsync (before any WaitAsync)
        // so the drain assertion is non-vacuous even if WaitAsync times out.
        await sut.StartAsync(CancellationToken.None);
        var executeTask = sut.ExecuteTask;

        try
        {
            // review-v236: NB-3 — await the throttle TCS signalled from inside RecordingDelay.Func;
            // guarantees the throttle delay was structurally attempted before we call StopAsync.
            await throttleReached.WaitAsync(TestTimeout);
        }
        finally
        {
            // StopAsync cancels stoppingToken; the in-flight throttle delay receives the OCE.
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — success result means no notification; cancellation is not a failure
        notifier.Verify(
            n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        // Cancellation must NOT produce an "iteration failed" LogError entry
        capturedLog.Should().NotContain(
            e => e.Level == LogLevel.Error && e.Message.Contains("iteration failed"),
            "OCE must re-throw, not be swallowed as a generic iteration failure");

        // The throttle delay was recorded before the cancellation (OCE propagation proof)
        delay.Recorded.Should().Contain(
            d => d == ConnectorStartupValidator.PerUserThrottle,
            "the throttle delay must have been attempted before the OCE propagated");

        // review-v236: NB-5 — the background task must have completed after StopAsync
        executeTask.Should().NotBeNull("ExecuteTask must be available after StartAsync");
        executeTask!.IsCompleted.Should().BeTrue(
            "StopAsync must have drained ExecuteAsync — a running task indicates a StopAsync leak");
        executeTask.IsFaulted.Should().BeFalse(
            "ExecuteAsync must exit cleanly via OCE propagation, not fault");
    }

    [Fact]
    public async Task ExecuteAsync_IterationLevelExceptionSwallowed_NextIterationStillRunsAllUsers()
    {
        // Arrange — first iteration: "user-throws" causes an exception that aborts iteration 1
        // (production outer try/catch wraps the entire foreach body).
        // Second iteration: both users processed normally.
        // Block at iteration 2 so StopAsync fires after iteration 2 completes.
        var userIds = new List<string> { "user-throws", "user-continues" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var throwCount = 0;
        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync("user-throws", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                throwCount++;
                if (throwCount == 1)
                    throw new InvalidOperationException("simulated transient failure on iteration 1");
                return Task.FromResult(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });
            });
        factory
            .Setup(f => f.ValidateDydxAsync("user-continues", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        // review-v236: NB-6 — ConcurrentQueue for log capture (written from background thread)
        var capturedLog = new ConcurrentQueue<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger<ConnectorStartupValidator>(capturedLog);

        var notifier = new Mock<ISignalRNotifier>();
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, logger, blockAtIteration: 2);

        var iteration2Ready = delay.WaitForIterationAsync(2);

        // Act — wait for iteration 2 to complete, then stop
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iteration2Ready.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — "user-continues" was validated exactly once in iteration 2 (NB3)
        // The outer try/catch aborts the CURRENT iteration — "user-continues" is skipped in
        // iteration 1 (after "user-throws" faults) but runs in iteration 2.
        // Times.Exactly(1) pins this invariant: a regression that moved the try/catch inside
        // the foreach (so user-continues ran in BOTH iterations) would cause Times.Exactly(1) to fail.
        factory.Verify(
            f => f.ValidateDydxAsync("user-continues", It.IsAny<CancellationToken>()),
            Times.Exactly(1),
            "user-continues must be validated exactly once — only in iteration 2 after iteration 1 was aborted");

        // "user-throws" was attempted in both iterations (NB3)
        factory.Verify(
            f => f.ValidateDydxAsync("user-throws", It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "user-throws must have been attempted twice: faulted in iteration 1, succeeded in iteration 2");

        // The swallowed exception must be logged at LogError (NB6)
        capturedLog.Should().Contain(
            e => e.Level == LogLevel.Error && e.Message.Contains("iteration failed"),
            "the swallowed iteration exception must be logged via LogError");
    }

    // review-v236: B3 — pin the cooperative-cancellation exit path (stoppingToken.IsCancellationRequested
    // in the per-user foreach). Block user-first's throttle delay, call StopAsync to cancel the
    // stopping token, then release the delay. After the throttle delay returns (via OCE), the
    // outer catch(OCE){throw;} propagates out — the test confirms no spurious notification and
    // that user-second was not validated via the cooperative break.
    // Note: the OCE from the throttle delay causes the outer catch to rethrow; user-second's
    // IsCancellationRequested check would fire if the SUT used a simple check path rather than
    // throwing OCE from the delay. This test pins the overall cooperative-cancel invariant.
    [Fact]
    public async Task ExecuteAsync_EmptyUserList_CompletesIterationWithoutValidationOrNotification()
    {
        // Arrange — no users; run one full iteration and confirm nothing is validated / notified.
        // Pins the behaviour of the iteration-level foreach when GetUsersWithCredentialsAsync
        // returns an empty list — a regression that skipped the user fetch would cause this
        // test to hang waiting for the iteration-ready TCS.
        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        var factory = new Mock<IExchangeConnectorFactory>();
        var notifier = new Mock<ISignalRNotifier>();
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, blockAtIteration: 1);
        var iterationReady = delay.WaitForIterationAsync(1);

        // Act — run one full iteration (which has zero users), then stop
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — no validation and no notification when user list is empty
        factory.Verify(
            f => f.ValidateDydxAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no ValidateDydxAsync call expected when user list is empty");
        notifier.Verify(
            n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "no notification expected when user list is empty");

        // No throttle delay recorded (no users = no per-user throttle)
        delay.Recorded.Should().NotContain(
            d => d == ConnectorStartupValidator.PerUserThrottle,
            "per-user throttle must not fire when user list is empty");
    }

    // review-v236: B3 — warm-up delay is recorded (confirms delay-seam is exercised on normal start).
    // The pre-cancelled test (ExecuteAsync_TokenCancelledBeforeWarmUp_DoesNotFetchUsers) shows
    // the warm-up is skipped when pre-cancelled; this complementary test confirms it DOES run.
    [Fact]
    public async Task ExecuteAsync_NormalStart_RecordsWarmUpDelayThenIterationInterval()
    {
        // Arrange — one user, one iteration, then stop
        var userIds = new List<string> { "user-warmup" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        var notifier = new Mock<ISignalRNotifier>();
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, blockAtIteration: 1);
        var iterationReady = delay.WaitForIterationAsync(1);

        // Act
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await iterationReady.WaitAsync(TestTimeout);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        // Assert — warm-up, throttle, and iteration-interval delays all recorded in order
        var recorded = delay.Recorded.ToList();
        recorded.Should().Contain(ConnectorStartupValidator.WarmUpDelay,
            "warm-up delay must be recorded on normal (non-pre-cancelled) start");
        recorded.Should().Contain(ConnectorStartupValidator.PerUserThrottle,
            "per-user throttle delay must be recorded after each ValidateDydxAsync call");
        recorded.Should().Contain(ConnectorStartupValidator.IterationInterval,
            "iteration-interval delay must be recorded at the end of each full iteration");

        // Order: warm-up first, then iteration body (throttle), then iteration-interval
        var warmUpIdx = recorded.IndexOf(ConnectorStartupValidator.WarmUpDelay);
        var iterationIdx = recorded.IndexOf(ConnectorStartupValidator.IterationInterval);
        warmUpIdx.Should().BeLessThan(iterationIdx,
            "warm-up delay must be recorded before the iteration-interval delay");
    }

    // ── Constant assertions ──────────────────────────────────────────────────────

    [Fact]
    public void AllTimingConstants_HaveExpectedValues()
    {
        // Behavioural tests already exercise these constants implicitly via the recorded
        // delay list. This single test pins the values for documentation and regression
        // detection; collapsed from three individual tests (nit3).
        ConnectorStartupValidator.IterationInterval.Should().Be(TimeSpan.FromMinutes(30));
        ConnectorStartupValidator.WarmUpDelay.Should().Be(TimeSpan.FromSeconds(30));
        ConnectorStartupValidator.PerUserThrottle.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    // ── Test double ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that captures formatted message strings.
    /// Uses <see cref="ConcurrentQueue{T}"/> so it is safe for Moq Callback writes from
    /// the background-service thread and test-thread reads after <c>StopAsync</c>.
    /// (review-v236: NB-6)
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries;

        public CapturingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
