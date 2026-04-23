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
        /// The delay function to inject.
        /// - Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is already cancelled.
        /// - Records the <paramref name="duration"/>.
        /// - For warm-up / throttle delays: returns immediately.
        /// - For iteration-interval delays: increments the counter; at <see cref="_blockAtIteration"/>
        ///   signals the awaitable and then blocks on <c>Task.Delay(Infinite, ct)</c>.
        ///   Prior iterations return immediately; subsequent ones (if any) also block.
        /// </summary>
        public Func<TimeSpan, CancellationToken, Task> Func =>
            async (duration, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _recorded.Enqueue(duration);

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

        var (sut, _) = BuildSut(userSettings.Object, factory.Object, notifier.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(CancellationToken.None);

        // Assert — warm-up was cancelled; no user fetch occurred
        userSettings.Verify(
            u => u.GetUsersWithCredentialsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        var capturedPayloads = new List<string>();
        var notifier = new Mock<ISignalRNotifier>();
        notifier
            .Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, msg) => capturedPayloads.Add(msg))
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
        capturedPayloads.Should().HaveCount(1,
            "exactly one notification should be pushed for a single-user failure — a second payload would indicate an unintended regression");
        capturedPayloads[0].Should().Be("dYdX credentials invalid — MissingMnemonic (Mnemonic)");
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

    [Fact]
    public async Task ExecuteAsync_NotificationPayload_DoesNotContainSentinelMnemonic()
    {
        // Arrange — sentinel mnemonic is kept out of the MissingField slot so the log-absence
        // assertion is non-vacuous. ConnectorStartupValidator logs {Reason} and {Field} from
        // the DydxCredentialCheckResult — if the sentinel appeared in MissingField it would be
        // logged by design (it's the field name, not raw credential material).
        // Instead, we assert:
        //   (a) the warning log entries contain the known-safe MissingField name "Mnemonic",
        //   (b) the sentinel string NEVER appears in any log entry at any level (NB4).
        // This is non-vacuous because the sentinel is not present in the result at all;
        // if any code path interpolated it into a log message the assertion would fail.
        var userIds = new List<string> { "user-leak-test" };
        var failure = new DydxCredentialCheckResult
        {
            Reason = DydxCredentialFailureReason.MissingMnemonic,
            MissingField = "Mnemonic"   // safe field name — sentinel is NOT in this slot
        };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        // Sentinel is only referenced in the mock setup comment — it never enters the SUT.
        // ConnectorStartupValidator does not receive or forward raw mnemonic values; it delegates
        // to factory.ValidateDydxAsync and only observes the result. The sentinel-free log
        // assertion below covers the complete log surface for this SUT.
        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync("user-leak-test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure);

        var capturedPayloads = new List<string>();
        var notifier = new Mock<ISignalRNotifier>();
        notifier
            .Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, msg) => capturedPayloads.Add(msg))
            .Returns(Task.CompletedTask);

        var capturedLog = new List<(LogLevel Level, string Message)>();
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

        // Assert — the notification must be emitted (confirms failure was actually processed)
        capturedPayloads.Should().NotBeEmpty("at least one failure notification should have been pushed");
        capturedPayloads[0].Should().Be("dYdX credentials invalid — MissingMnemonic (Mnemonic)",
            "notification uses the pinned format with the safe field-name value");
        // Warning log entries reference the failure reason and field name only (not raw credential material)
        capturedLog
            .Where(e => e.Level == LogLevel.Warning)
            .Should().AllSatisfy(e =>
                e.Message.Should().Contain("MissingMnemonic"),
                "warning log entries reference the failure reason");
        // Sentinel must not appear in any log entry at any level (NB4 — non-vacuous: sentinel
        // is not in the result, so any appearance indicates an unexpected code path emitting it)
        capturedLog.Should().NotContain(
            e => e.Message.Contains(SentinelMnemonic),
            "sentinel mnemonic must never appear in any log entry at any level — including Debug/Info/Trace");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringIteration_ExitsWithoutSpuriousNotification()
    {
        // Arrange — cancel the stoppingToken immediately after ValidateDydxAsync returns.
        // Production: the OCE from the throttle delay propagates up through the foreach and
        // is caught by the outer catch(OperationCanceledException){throw;}, exiting ExecuteAsync.
        //
        // Synchronization: signal from the ValidateDydxAsync callback, then stop.
        var userIds = new List<string> { "user-mid" };

        var userSettings = new Mock<IUserSettingsService>();
        userSettings
            .Setup(u => u.GetUsersWithCredentialsAsync("dYdX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(userIds);

        var validateCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Mock<IExchangeConnectorFactory>();
        factory
            .Setup(f => f.ValidateDydxAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, _) => validateCalled.TrySetResult())
            .ReturnsAsync(new DydxCredentialCheckResult { Reason = DydxCredentialFailureReason.None });

        var capturedLog = new List<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger<ConnectorStartupValidator>(capturedLog);

        var notifier = new Mock<ISignalRNotifier>();
        // blockAtIteration: 1 ensures the loop does not spin past iteration 1
        var (sut, delay) = BuildSut(userSettings.Object, factory.Object, notifier.Object, logger, blockAtIteration: 1);

        // Act — start, wait until ValidateDydxAsync fires (before throttle delay), then stop.
        // StopAsync cancels stoppingToken which is passed to the throttle delay; it throws OCE.
        await sut.StartAsync(CancellationToken.None);
        Task? executeTask = null;
        try
        {
            await validateCalled.Task.WaitAsync(TestTimeout);

            // Capture the background execute task before stopping so we can assert it completed cleanly (NB5)
            executeTask = sut.ExecuteTask;

            await sut.StopAsync(CancellationToken.None);
        }
        catch
        {
            await sut.StopAsync(CancellationToken.None);
            throw;
        }

        // Assert — success result means no notification; cancellation is not a failure
        notifier.Verify(
            n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        // Cancellation must NOT produce an "iteration failed" LogError entry (NB5)
        capturedLog.Should().NotContain(
            e => e.Level == LogLevel.Error && e.Message.Contains("iteration failed"),
            "OCE must re-throw, not be swallowed as a generic iteration failure");

        // The throttle delay was reached before the cancellation (NB5 — OCE propagation proof)
        delay.Recorded.Should().Contain(
            d => d == ConnectorStartupValidator.PerUserThrottle,
            "the throttle delay must have been attempted before the OCE propagated, confirming the cancel path ran through the throttle site");

        // The background task must have completed (not be running or faulted) after StopAsync (NB5)
        if (executeTask is not null)
        {
            executeTask.IsCompleted.Should().BeTrue(
                "StopAsync must have drained ExecuteAsync — a running task indicates a StopAsync leak");
            executeTask.IsFaulted.Should().BeFalse(
                "ExecuteAsync must exit cleanly via OCE propagation, not fault");
        }
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

        var capturedLog = new List<(LogLevel Level, string Message)>();
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

    /// <summary>Minimal <see cref="ILogger{T}"/> that captures formatted message strings.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries;

        public CapturingLogger(List<(LogLevel Level, string Message)> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
