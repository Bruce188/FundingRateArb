using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddDexCredentialFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EncryptedSubAccountAddress",
            table: "UserExchangeCredentials",
            type: "nvarchar(2000)",
            maxLength: 2000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EncryptedApiKeyIndex",
            table: "UserExchangeCredentials",
            type: "nvarchar(2000)",
            maxLength: 2000,
            nullable: true);

        // Data migration: for existing Lighter credentials, copy the encrypted API key
        // value to the new ApiKeyIndex column (already-encrypted ciphertext, no re-encryption needed)
        migrationBuilder.Sql(@"
            UPDATE uc SET uc.EncryptedApiKeyIndex = uc.EncryptedApiKey
            FROM UserExchangeCredentials uc
            INNER JOIN Exchanges e ON uc.ExchangeId = e.Id
            WHERE e.Name = 'Lighter' AND uc.EncryptedApiKey IS NOT NULL
        ");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EncryptedSubAccountAddress",
            table: "UserExchangeCredentials");

        migrationBuilder.DropColumn(
            name: "EncryptedApiKeyIndex",
            table: "UserExchangeCredentials");
    }
}
