using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddFundingAccuracyProperties : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FundingNotionalPriceType",
            table: "Exchanges",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<decimal>(
            name: "FundingRebateRate",
            table: "Exchanges",
            type: "decimal(5,4)",
            precision: 5,
            scale: 4,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<int>(
            name: "FundingTimingDeviationSeconds",
            table: "Exchanges",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FundingNotionalPriceType",
            table: "Exchanges");

        migrationBuilder.DropColumn(
            name: "FundingRebateRate",
            table: "Exchanges");

        migrationBuilder.DropColumn(
            name: "FundingTimingDeviationSeconds",
            table: "Exchanges");
    }
}
