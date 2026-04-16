namespace FundingRateArb.Application.DTOs;

/// <summary>
/// Carries per-field credential status for dYdX. Never contains credential values —
/// only presence/validity booleans, the failure reason enum, and the field name string.
/// </summary>
public record DydxCredentialCheckResult
{
    public bool MnemonicPresent { get; init; }
    public bool MnemonicValidBip39 { get; init; }
    public bool SubAccountPresent { get; init; }
    public bool DerivedAddressValid { get; init; }
    public bool IndexerReachable { get; init; }
    public DydxCredentialFailureReason Reason { get; init; }

    /// <summary>Field name (e.g. "Mnemonic", "SubAccountAddress", or "" when Reason == None). Never the field value.</summary>
    public string MissingField { get; init; } = "";
}
