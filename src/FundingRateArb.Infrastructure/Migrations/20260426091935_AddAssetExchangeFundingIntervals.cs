using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetExchangeFundingIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssetExchangeFundingIntervals",
                columns: table => new
                {
                    ExchangeId = table.Column<int>(type: "int", nullable: false),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    IntervalHours = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceSnapshotId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetExchangeFundingIntervals", x => new { x.ExchangeId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_AssetExchangeFundingIntervals_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetExchangeFundingIntervals_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetExchangeFundingIntervals_FundingRateSnapshots_SourceSnapshotId",
                        column: x => x.SourceSnapshotId,
                        principalTable: "FundingRateSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetExchangeFundingIntervals_AssetId",
                table: "AssetExchangeFundingIntervals",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetExchangeFundingIntervals_SourceSnapshotId",
                table: "AssetExchangeFundingIntervals",
                column: "SourceSnapshotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetExchangeFundingIntervals");
        }
    }
}
