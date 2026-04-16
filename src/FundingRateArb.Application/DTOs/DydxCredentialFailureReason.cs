namespace FundingRateArb.Application.DTOs;

public enum DydxCredentialFailureReason
{
    None,
    MissingMnemonic,
    InvalidMnemonic,
    MissingSubAccount,
    DerivedAddressInvalid,
    SignerConstructionFailed,
    IndexerUnreachable
}
