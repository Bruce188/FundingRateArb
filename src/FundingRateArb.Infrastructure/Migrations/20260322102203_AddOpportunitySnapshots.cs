using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunitySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FundingWindowMinutes",
                table: "UserConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FundingSettlementType",
                table: "Exchanges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FundingWindowMinutes",
                table: "BotConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "FundingRateHourlyAggregates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExchangeId = table.Column<int>(type: "int", nullable: false),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    HourUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AvgRatePerHour = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    MinRate = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    MaxRate = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    LastRate = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    AvgVolume24hUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AvgMarkPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FundingRateHourlyAggregates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FundingRateHourlyAggregates_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FundingRateHourlyAggregates_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpportunitySnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    LongExchangeId = table.Column<int>(type: "int", nullable: false),
                    ShortExchangeId = table.Column<int>(type: "int", nullable: false),
                    SpreadPerHour = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    NetYieldPerHour = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    LongVolume24h = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ShortVolume24h = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WasOpened = table.Column<bool>(type: "bit", nullable: false),
                    SkipReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunitySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunitySnapshots_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpportunitySnapshots_Exchanges_LongExchangeId",
                        column: x => x.LongExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunitySnapshots_Exchanges_ShortExchangeId",
                        column: x => x.ShortExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FundingRateHourlyAggregates_AssetId",
                table: "FundingRateHourlyAggregates",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_FundingRateHourlyAggregates_ExchangeId_AssetId_HourUtc",
                table: "FundingRateHourlyAggregates",
                columns: new[] { "ExchangeId", "AssetId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySnapshots_AssetId_RecordedAt",
                table: "OpportunitySnapshots",
                columns: new[] { "AssetId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySnapshots_LongExchangeId",
                table: "OpportunitySnapshots",
                column: "LongExchangeId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySnapshots_RecordedAt",
                table: "OpportunitySnapshots",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySnapshots_ShortExchangeId",
                table: "OpportunitySnapshots",
                column: "ShortExchangeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FundingRateHourlyAggregates");

            migrationBuilder.DropTable(
                name: "OpportunitySnapshots");

            migrationBuilder.DropColumn(
                name: "FundingWindowMinutes",
                table: "UserConfigurations");

            migrationBuilder.DropColumn(
                name: "FundingSettlementType",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "FundingWindowMinutes",
                table: "BotConfigurations");
        }
    }
}
