using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderAttemptCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LongOrderAttemptN",
                table: "ArbitragePositions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShortOrderAttemptN",
                table: "ArbitragePositions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LongOrderAttemptN",
                table: "ArbitragePositions");

            migrationBuilder.DropColumn(
                name: "ShortOrderAttemptN",
                table: "ArbitragePositions");
        }
    }
}
