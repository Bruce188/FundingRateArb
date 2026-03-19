using FundingRateArb.Application.Services;
using Microsoft.AspNetCore.DataProtection;

namespace FundingRateArb.Infrastructure.Services;

public class ApiKeyVault : IApiKeyVault
{
    private readonly IDataProtector _protector;

    public ApiKeyVault(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("FundingRateArb.ApiKeys");
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
