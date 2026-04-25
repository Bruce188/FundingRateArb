using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArbitragePositionsUserIdStatusCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArbitragePositions_UserId",
                table: "ArbitragePositions");

            migrationBuilder.CreateIndex(
                name: "IX_ArbitragePositions_UserId_Status",
                table: "ArbitragePositions",
                columns: new[] { "UserId", "Status" })
                .Annotation("SqlServer:Include", new[] { "RealizedPnl", "IsPhantomFeeBackfill" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArbitragePositions_UserId_Status",
                table: "ArbitragePositions");

            migrationBuilder.CreateIndex(
                name: "IX_ArbitragePositions_UserId",
                table: "ArbitragePositions",
                column: "UserId");
        }
    }
}
