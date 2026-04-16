using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Interfaces;

/// <summary>
/// Encapsulates per-field validation and construction of a dYdX connector.
/// Implementations live in Infrastructure; the interface lives in Application so both
/// Infrastructure and Web can reference it without a reverse dependency.
/// </summary>
public interface IDydxConnectorFactory
{
    /// <summary>
    /// Synchronous, exception-free validation of mnemonic and optional sub-account address.
    /// Returns a <see cref="DydxCredentialCheckResult"/> with per-field status flags.
    /// Never throws; all exceptions are mapped to failure reasons.
    /// </summary>
    DydxCredentialCheckResult Validate(string? mnemonic, string? subAccountAddress);

    /// <summary>
    /// Runs <see cref="Validate"/> and, if that passes, performs a signed no-op HTTP call
    /// against the dYdX indexer to confirm the derived address is reachable.
    /// </summary>
    Task<DydxCredentialCheckResult> ValidateSignedAsync(
        string? mnemonic, string? subAccountAddress, CancellationToken ct);

    /// <summary>
    /// Attempts to create a dYdX connector. Returns <c>false</c> and
    /// <c>connector = null</c> when <see cref="Validate"/> fails; otherwise returns
    /// <c>true</c> and a fully initialised connector.
    /// </summary>
    bool TryCreate(
        string? mnemonic, string? subAccountAddress,
        out IExchangeConnector? connector, out DydxCredentialCheckResult result);
}
