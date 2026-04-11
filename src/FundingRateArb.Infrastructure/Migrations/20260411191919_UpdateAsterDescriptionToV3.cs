using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAsterDescriptionToV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Exchanges SET Description = 'Aster DEX — EIP-712 Pro API, USDT collateral, 8-hour funding' WHERE Name = 'Aster'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Exchanges SET Description = 'Aster DEX — HMAC-SHA256, USDT collateral, 8-hour funding' WHERE Name = 'Aster'");
        }
    }
}
