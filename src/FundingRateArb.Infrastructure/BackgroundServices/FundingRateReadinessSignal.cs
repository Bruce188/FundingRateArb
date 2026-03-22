using FundingRateArb.Application.Common.Exchanges;

namespace FundingRateArb.Infrastructure.BackgroundServices;

/// <summary>
/// TaskCompletionSource-based readiness signal. Registered as singleton so both
/// FundingRateFetcher and BotOrchestrator share the same instance.
/// </summary>
public class FundingRateReadinessSignal : IFundingRateReadinessSignal
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task WaitForReadyAsync(CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var delayTask = Task.Delay(Timeout, linkedCts.Token);
        var completedTask = await Task.WhenAny(_tcs.Task, delayTask);
        _ = delayTask.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default);

        // If the TCS completed, we're ready. If the delay completed first (timeout), proceed anyway.
        // If cancellation was requested, the Task.Delay throws and propagates.
        if (completedTask != _tcs.Task && ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();
    }

    public void SignalReady() => _tcs.TrySetResult(true);
}
