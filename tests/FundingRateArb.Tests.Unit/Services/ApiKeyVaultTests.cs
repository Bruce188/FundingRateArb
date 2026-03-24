using FluentAssertions;
using FundingRateArb.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;

namespace FundingRateArb.Tests.Unit.Services;

public class ApiKeyVaultTests
{
    private readonly ApiKeyVault _sut;

    public ApiKeyVaultTests()
    {
        // Use the in-memory ephemeral provider (no key persistence needed for unit tests)
        var provider = new EphemeralDataProtectionProvider();
        _sut = new ApiKeyVault(provider);
    }

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginalValue()
    {
        const string plaintext = "super-secret-api-key-12345";

        var ciphertext = _sut.Encrypt(plaintext);
        var result = _sut.Decrypt(ciphertext);

        result.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputThanInput()
    {
        const string plaintext = "my-private-key";

        var ciphertext = _sut.Encrypt(plaintext);

        ciphertext.Should().NotBe(plaintext);
    }

    [Theory]
    [InlineData("0xABCDEF1234567890")]
    [InlineData("some-api-key")]
    [InlineData("")]
    public void RoundTrip_WorksForVariousInputs(string plaintext)
    {
        var ciphertext = _sut.Encrypt(plaintext);
        var result = _sut.Decrypt(ciphertext);

        result.Should().Be(plaintext);
    }
}
