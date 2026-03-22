using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRebalanceConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RebalanceEnabled",
                table: "BotConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "RebalanceMinImprovement",
                table: "BotConfigurations",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0.0002m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RebalanceEnabled",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "RebalanceMinImprovement",
                table: "BotConfigurations");
        }
    }
}
