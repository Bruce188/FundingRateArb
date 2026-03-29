using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddExchangeCircuitBreakerConfig : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ExchangeCircuitBreakerMinutes",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 15);

        migrationBuilder.AddColumn<int>(
            name: "ExchangeCircuitBreakerThreshold",
            table: "BotConfigurations",
            type: "int",
            nullable: false,
            defaultValue: 3);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExchangeCircuitBreakerMinutes",
            table: "BotConfigurations");

        migrationBuilder.DropColumn(
            name: "ExchangeCircuitBreakerThreshold",
            table: "BotConfigurations");
    }
}
