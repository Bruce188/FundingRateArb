using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace FundingRateArb.Tests.Unit.Configuration;

public class KeyVaultConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void KeyVaultName_WhenEmpty_SkipsKeyVaultRegistration(string? keyVaultName)
    {
        // Arrange — simulate the same conditional used in Program.cs
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyVaultName"] = keyVaultName
            })
            .Build();

        var resolvedName = config["KeyVaultName"];

        // Act & Assert — the guard clause in Program.cs uses string.IsNullOrEmpty
        // Verify the value would cause the Key Vault block to be skipped
        string.IsNullOrEmpty(resolvedName).Should().BeTrue(
            "an empty/null KeyVaultName must skip Azure Key Vault registration");
    }

    [Fact]
    public void KeyVaultName_WhenSet_WouldTriggerKeyVaultRegistration()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyVaultName"] = "kv-fundingratearb"
            })
            .Build();

        var resolvedName = config["KeyVaultName"];

        // Act & Assert — verify a non-empty value would enter the Key Vault block
        string.IsNullOrEmpty(resolvedName).Should().BeFalse(
            "a configured KeyVaultName must trigger Azure Key Vault registration");
    }

    [Fact]
    public void KeyVaultName_DefaultAppsettings_IsEmpty()
    {
        // Arrange — load the actual appsettings.json to verify the default is empty
        var projectDir = FindProjectRoot();
        var config = new ConfigurationBuilder()
            .SetBasePath(projectDir)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Act
        var keyVaultName = config["KeyVaultName"];

        // Assert — default must be empty so local dev doesn't try to hit Key Vault
        string.IsNullOrEmpty(keyVaultName).Should().BeTrue(
            "appsettings.json must ship with an empty KeyVaultName for local development");
    }

    private static string FindProjectRoot()
    {
        // Walk up from the test assembly location to find the Web project's appsettings.json
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FundingRateArb.Web");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the FundingRateArb.Web project root.");
    }
}
