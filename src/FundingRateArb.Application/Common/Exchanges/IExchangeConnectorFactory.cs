using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IExchangeConnectorFactory
{
    IExchangeConnector GetConnector(string exchangeName);
    IEnumerable<IExchangeConnector> GetAllConnectors();

    /// <summary>
    /// Creates a short-lived connector instance with user-specific credentials.
    /// Returns null if the exchange name is unknown or credentials are insufficient.
    /// When <paramref name="userId"/> is non-null and the exchange is dYdX, the failure reason
    /// is stored in an internal cache readable via <see cref="TryGetLastDydxFailure"/>.
    /// </summary>
    Task<IExchangeConnector?> CreateForUserAsync(
        string exchangeName,
        string? apiKey,
        string? apiSecret,
        string? walletAddress,
        string? privateKey,
        string? subAccountAddress,
        string? apiKeyIndex,
        string? userId);

    /// <summary>
    /// Performs a full per-field dYdX credential validation (including a signed HTTP no-op)
    /// for the specified user. Decrypts credentials internally. Stores result in the
    /// per-user failure cache so <see cref="TryGetLastDydxFailure"/> is up-to-date.
    /// </summary>
    Task<DydxCredentialCheckResult> ValidateDydxAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Returns the most recent <see cref="DydxCredentialCheckResult"/> recorded for the
    /// given user, or <c>false</c> if no failure has been recorded yet.
    /// </summary>
    bool TryGetLastDydxFailure(string userId, out DydxCredentialCheckResult result);
}
