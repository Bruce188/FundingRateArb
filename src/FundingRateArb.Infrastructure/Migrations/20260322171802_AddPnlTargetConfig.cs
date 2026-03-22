using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPnlTargetConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveHoldEnabled",
                table: "BotConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetPnlMultiplier",
                table: "BotConfigurations",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 2.0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdaptiveHoldEnabled",
                table: "BotConfigurations");

            migrationBuilder.DropColumn(
                name: "TargetPnlMultiplier",
                table: "BotConfigurations");
        }
    }
}
