namespace FundingRateArb.Application.Common.Interfaces;

public interface IConnectivityTestService
{
    Task<ConnectivityTestResult> RunTestAsync(
        string adminUserId, string targetUserId, int exchangeId, CancellationToken ct = default);
}

public record ConnectivityTestResult(
    bool Success,
    string ExchangeName,
    string? Error = null,
    decimal? Balance = null);
