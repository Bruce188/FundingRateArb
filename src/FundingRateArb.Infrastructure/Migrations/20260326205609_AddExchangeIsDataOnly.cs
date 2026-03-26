using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddExchangeIsDataOnly : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDataOnly",
            table: "Exchanges",
            type: "bit",
            nullable: false,
            defaultValue: false);

        // Set CoinGlass as data-only (read-only aggregator, not a trading venue)
        migrationBuilder.Sql("UPDATE Exchanges SET IsDataOnly = 1 WHERE Name = 'CoinGlass'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsDataOnly",
            table: "Exchanges");
    }
}
