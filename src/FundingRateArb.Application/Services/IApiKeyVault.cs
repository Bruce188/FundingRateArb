namespace FundingRateArb.Application.Services;

public interface IApiKeyVault
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}
