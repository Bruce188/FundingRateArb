using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiUserEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAssetPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAssetPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAssetPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserAssetPreferences_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OpenThreshold = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                    CloseThreshold = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                    AlertThreshold = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                    DefaultLeverage = table.Column<int>(type: "int", nullable: false),
                    TotalCapitalUsdc = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MaxCapitalPerPosition = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MaxConcurrentPositions = table.Column<int>(type: "int", nullable: false),
                    StopLossPct = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MaxHoldTimeHours = table.Column<int>(type: "int", nullable: false),
                    AllocationStrategy = table.Column<int>(type: "int", nullable: false),
                    AllocationTopN = table.Column<int>(type: "int", nullable: false),
                    FeeAmortizationHours = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MinPositionSizeUsdc = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MinVolume24hUsdc = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RateStalenessMinutes = table.Column<int>(type: "int", nullable: false),
                    DailyDrawdownPausePct = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConsecutiveLossPause = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserConfigurations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserExchangeCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ExchangeId = table.Column<int>(type: "int", nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EncryptedApiSecret = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EncryptedWalletAddress = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EncryptedPrivateKey = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExchangeCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserExchangeCredentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserExchangeCredentials_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserExchangePreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ExchangeId = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExchangePreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserExchangePreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserExchangePreferences_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAssetPreferences_AssetId",
                table: "UserAssetPreferences",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAssetPreferences_UserId_AssetId",
                table: "UserAssetPreferences",
                columns: new[] { "UserId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserConfigurations_UserId",
                table: "UserConfigurations",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExchangeCredentials_ExchangeId",
                table: "UserExchangeCredentials",
                column: "ExchangeId");

            migrationBuilder.CreateIndex(
                name: "IX_UserExchangeCredentials_UserId_ExchangeId",
                table: "UserExchangeCredentials",
                columns: new[] { "UserId", "ExchangeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExchangePreferences_ExchangeId",
                table: "UserExchangePreferences",
                column: "ExchangeId");

            migrationBuilder.CreateIndex(
                name: "IX_UserExchangePreferences_UserId_ExchangeId",
                table: "UserExchangePreferences",
                columns: new[] { "UserId", "ExchangeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAssetPreferences");

            migrationBuilder.DropTable(
                name: "UserConfigurations");

            migrationBuilder.DropTable(
                name: "UserExchangeCredentials");

            migrationBuilder.DropTable(
                name: "UserExchangePreferences");
        }
    }
}
