using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;

/// <summary>
/// Factory that encapsulates per-field dYdX credential validation and connector construction.
/// Registered as singleton — all state is local to each call.
/// </summary>
public sealed class DydxConnectorFactory : IDydxConnectorFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly ILogger<DydxConnectorFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMarkPriceCache _markPriceCache;

    public DydxConnectorFactory(
        IHttpClientFactory httpClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<DydxConnectorFactory> logger,
        ILoggerFactory loggerFactory,
        IMarkPriceCache markPriceCache)
    {
        _httpClientFactory = httpClientFactory;
        _pipelineProvider = pipelineProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _markPriceCache = markPriceCache;
    }

    /// <inheritdoc />
    public DydxCredentialCheckResult Validate(string? mnemonic, string? subAccountAddress)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            return new DydxCredentialCheckResult
            {
                Reason = DydxCredentialFailureReason.MissingMnemonic,
                MissingField = "Mnemonic"
            };
        }

        bool mnemonicValidBip39;
        try
        {
            // Attempt signer construction to validate the mnemonic.
            using var _ = new DydxSigner(mnemonic);
            mnemonicValidBip39 = true;
        }
        catch (ArgumentException)
        {
            return new DydxCredentialCheckResult
            {
                MnemonicPresent = true,
                Reason = DydxCredentialFailureReason.InvalidMnemonic,
                MissingField = "Mnemonic"
            };
        }
        catch (FormatException)
        {
            return new DydxCredentialCheckResult
            {
                MnemonicPresent = true,
                Reason = DydxCredentialFailureReason.InvalidMnemonic,
                MissingField = "Mnemonic"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("dYdX signer construction failed — {Reason}", ex.GetType().Name);
            return new DydxCredentialCheckResult
            {
                MnemonicPresent = true,
                Reason = DydxCredentialFailureReason.SignerConstructionFailed
            };
        }

        // Sub-account is optional — never set MissingSubAccount in the current iteration.
        var subAccountPresent = !string.IsNullOrWhiteSpace(subAccountAddress);

        return new DydxCredentialCheckResult
        {
            MnemonicPresent = true,
            MnemonicValidBip39 = mnemonicValidBip39,
            SubAccountPresent = subAccountPresent,
            DerivedAddressValid = true,
            Reason = DydxCredentialFailureReason.None
        };
    }

    /// <inheritdoc />
    public async Task<DydxCredentialCheckResult> ValidateSignedAsync(
        string? mnemonic, string? subAccountAddress, CancellationToken ct)
    {
        var syncResult = Validate(mnemonic, subAccountAddress);
        if (syncResult.Reason != DydxCredentialFailureReason.None)
        {
            return syncResult;
        }

        // Build signer to get the derived address for the signed request.
        DydxSigner signer;
        try
        {
            signer = new DydxSigner(mnemonic!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("dYdX signer construction failed during signed validation — {Reason}", ex.GetType().Name);
            return new DydxCredentialCheckResult
            {
                MnemonicPresent = true,
                Reason = DydxCredentialFailureReason.SignerConstructionFailed
            };
        }

        using (signer)
        {
            var validatorClient = _httpClientFactory.CreateClient("DydxValidator");
            try
            {
                var response = await validatorClient.GetAsync(
                    $"addresses/{signer.Address}/subaccountNumber/0", ct);

                if (response.IsSuccessStatusCode)
                {
                    return new DydxCredentialCheckResult
                    {
                        MnemonicPresent = true,
                        MnemonicValidBip39 = true,
                        SubAccountPresent = !string.IsNullOrWhiteSpace(subAccountAddress),
                        DerivedAddressValid = true,
                        IndexerReachable = true,
                        Reason = DydxCredentialFailureReason.None
                    };
                }

                // 401 / 403 / 404 — the signed request failed authoritatively.
                _logger.LogWarning(
                    "dYdX indexer returned {StatusCode} for credential check — {Reason}",
                    (int)response.StatusCode, DydxCredentialFailureReason.DerivedAddressInvalid);
                return new DydxCredentialCheckResult
                {
                    MnemonicPresent = true,
                    MnemonicValidBip39 = true,
                    SubAccountPresent = !string.IsNullOrWhiteSpace(subAccountAddress),
                    DerivedAddressValid = false,
                    IndexerReachable = false,
                    Reason = DydxCredentialFailureReason.DerivedAddressInvalid
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("dYdX indexer unreachable during credential check — {Reason}", ex.GetType().Name);
                return new DydxCredentialCheckResult
                {
                    MnemonicPresent = true,
                    MnemonicValidBip39 = true,
                    SubAccountPresent = !string.IsNullOrWhiteSpace(subAccountAddress),
                    IndexerReachable = false,
                    Reason = DydxCredentialFailureReason.IndexerUnreachable
                };
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Timeout (TaskCanceledException with the client's internal CTS, not the caller's).
                _logger.LogWarning("dYdX credential check timed out — {Reason}", ex.GetType().Name);
                return new DydxCredentialCheckResult
                {
                    MnemonicPresent = true,
                    MnemonicValidBip39 = true,
                    SubAccountPresent = !string.IsNullOrWhiteSpace(subAccountAddress),
                    IndexerReachable = false,
                    Reason = DydxCredentialFailureReason.IndexerUnreachable
                };
            }
        }
    }

    /// <inheritdoc />
    public bool TryCreate(
        string? mnemonic, string? subAccountAddress,
        out IExchangeConnector? connector, out DydxCredentialCheckResult result)
    {
        result = Validate(mnemonic, subAccountAddress);
        if (result.Reason != DydxCredentialFailureReason.None)
        {
            connector = null;
            return false;
        }

        try
        {
            var signer = new DydxSigner(mnemonic!);
            var indexerClient = _httpClientFactory.CreateClient("DydxIndexer");
            var validatorClient = _httpClientFactory.CreateClient("DydxValidator");
            var connectorLogger = _loggerFactory.CreateLogger<DydxConnector>();
            connector = new DydxConnector(
                indexerClient, validatorClient, signer,
                _pipelineProvider, connectorLogger, _markPriceCache);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("dYdX connector instantiation failed — {Reason}", ex.GetType().Name);
            result = new DydxCredentialCheckResult
            {
                MnemonicPresent = true,
                Reason = DydxCredentialFailureReason.SignerConstructionFailed
            };
            connector = null;
            return false;
        }
    }
}
