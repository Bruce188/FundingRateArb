using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddFundingRateCompositeIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_FundingRateSnapshots_ExchangeId_AssetId_RecordedAt",
            table: "FundingRateSnapshots");

        migrationBuilder.CreateIndex(
            name: "IX_FundingRateSnapshots_Exchange_Asset_RecordedAt",
            table: "FundingRateSnapshots",
            columns: new[] { "ExchangeId", "AssetId", "RecordedAt" },
            descending: new[] { false, false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_FundingRateSnapshots_Exchange_Asset_RecordedAt",
            table: "FundingRateSnapshots");

        migrationBuilder.CreateIndex(
            name: "IX_FundingRateSnapshots_ExchangeId_AssetId_RecordedAt",
            table: "FundingRateSnapshots",
            columns: new[] { "ExchangeId", "AssetId", "RecordedAt" });
    }
}
