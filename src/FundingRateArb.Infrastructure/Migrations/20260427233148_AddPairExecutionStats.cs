using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddPairExecutionStats : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PairAutoDenyEnabled",
            table: "BotConfigurations",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "PairExecutionStats",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                LongExchangeName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ShortExchangeName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                WindowStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                WindowEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                CloseCount = table.Column<int>(type: "int", nullable: false),
                WinCount = table.Column<int>(type: "int", nullable: false),
                TotalPnlUsdc = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                AvgHoldSec = table.Column<int>(type: "int", nullable: false),
                IsDenied = table.Column<bool>(type: "bit", nullable: false),
                DeniedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                DeniedReason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PairExecutionStats", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PairExecutionStats_LongExchangeName_ShortExchangeName",
            table: "PairExecutionStats",
            columns: new[] { "LongExchangeName", "ShortExchangeName" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PairExecutionStats");

        migrationBuilder.DropColumn(
            name: "PairAutoDenyEnabled",
            table: "BotConfigurations");
    }
}
