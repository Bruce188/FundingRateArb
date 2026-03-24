using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AspNetRoles",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                AccessFailedCount = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUsers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Assets",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Assets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BotConfigurations",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                OpenThreshold = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                AlertThreshold = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                CloseThreshold = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                StopLossPct = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                MaxHoldTimeHours = table.Column<int>(type: "int", nullable: false),
                VolumeFraction = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                MaxCapitalPerPosition = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                BreakevenHoursMax = table.Column<int>(type: "int", nullable: false),
                TotalCapitalUsdc = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                DefaultLeverage = table.Column<int>(type: "int", nullable: false),
                MaxConcurrentPositions = table.Column<int>(type: "int", nullable: false),
                LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BotConfigurations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Exchanges",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ApiBaseUrl = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                WsBaseUrl = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                FundingInterval = table.Column<int>(type: "int", nullable: false),
                FundingIntervalHours = table.Column<int>(type: "int", nullable: false),
                SupportsSubAccounts = table.Column<bool>(type: "bit", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Exchanges", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetRoleClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                UserId = table.Column<string>(type: "nvarchar(450)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserRoles",
            columns: table => new
            {
                UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ArbitragePositions",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                AssetId = table.Column<int>(type: "int", nullable: false),
                LongExchangeId = table.Column<int>(type: "int", nullable: false),
                ShortExchangeId = table.Column<int>(type: "int", nullable: false),
                SizeUsdc = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                MarginUsdc = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Leverage = table.Column<int>(type: "int", nullable: false),
                LongEntryPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                ShortEntryPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                EntrySpreadPerHour = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                CurrentSpreadPerHour = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                AccumulatedFunding = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                RealizedPnl = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                Status = table.Column<int>(type: "int", nullable: false),
                CloseReason = table.Column<int>(type: "int", nullable: true),
                LongOrderId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                ShortOrderId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ArbitragePositions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ArbitragePositions_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ArbitragePositions_Assets_AssetId",
                    column: x => x.AssetId,
                    principalTable: "Assets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ArbitragePositions_Exchanges_LongExchangeId",
                    column: x => x.LongExchangeId,
                    principalTable: "Exchanges",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ArbitragePositions_Exchanges_ShortExchangeId",
                    column: x => x.ShortExchangeId,
                    principalTable: "Exchanges",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ExchangeAssetConfigs",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ExchangeId = table.Column<int>(type: "int", nullable: false),
                AssetId = table.Column<int>(type: "int", nullable: false),
                SizeDecimals = table.Column<int>(type: "int", nullable: false),
                MinOrderSize = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                StepSize = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                PriceDecimals = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExchangeAssetConfigs", x => x.Id);
                table.ForeignKey(
                    name: "FK_ExchangeAssetConfigs_Assets_AssetId",
                    column: x => x.AssetId,
                    principalTable: "Assets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ExchangeAssetConfigs_Exchanges_ExchangeId",
                    column: x => x.ExchangeId,
                    principalTable: "Exchanges",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FundingRateSnapshots",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ExchangeId = table.Column<int>(type: "int", nullable: false),
                AssetId = table.Column<int>(type: "int", nullable: false),
                RatePerHour = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                RawRate = table.Column<decimal>(type: "decimal(18,10)", nullable: false),
                MarkPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                IndexPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                Volume24hUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FundingRateSnapshots", x => x.Id);
                table.ForeignKey(
                    name: "FK_FundingRateSnapshots_Assets_AssetId",
                    column: x => x.AssetId,
                    principalTable: "Assets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_FundingRateSnapshots_Exchanges_ExchangeId",
                    column: x => x.ExchangeId,
                    principalTable: "Exchanges",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Alerts",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ArbitragePositionId = table.Column<int>(type: "int", nullable: true),
                Type = table.Column<int>(type: "int", nullable: false),
                Severity = table.Column<int>(type: "int", nullable: false),
                Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                IsRead = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Alerts", x => x.Id);
                table.ForeignKey(
                    name: "FK_Alerts_ArbitragePositions_ArbitragePositionId",
                    column: x => x.ArbitragePositionId,
                    principalTable: "ArbitragePositions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Alerts_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Alerts_ArbitragePositionId",
            table: "Alerts",
            column: "ArbitragePositionId");

        migrationBuilder.CreateIndex(
            name: "IX_Alerts_UserId_CreatedAt",
            table: "Alerts",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ArbitragePositions_AssetId",
            table: "ArbitragePositions",
            column: "AssetId");

        migrationBuilder.CreateIndex(
            name: "IX_ArbitragePositions_LongExchangeId",
            table: "ArbitragePositions",
            column: "LongExchangeId");

        migrationBuilder.CreateIndex(
            name: "IX_ArbitragePositions_ShortExchangeId",
            table: "ArbitragePositions",
            column: "ShortExchangeId");

        migrationBuilder.CreateIndex(
            name: "IX_ArbitragePositions_UserId",
            table: "ArbitragePositions",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetRoleClaims_RoleId",
            table: "AspNetRoleClaims",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "AspNetRoles",
            column: "NormalizedName",
            unique: true,
            filter: "[NormalizedName] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserClaims_UserId",
            table: "AspNetUserClaims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserLogins_UserId",
            table: "AspNetUserLogins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserRoles_RoleId",
            table: "AspNetUserRoles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "NormalizedEmail");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "NormalizedUserName",
            unique: true,
            filter: "[NormalizedUserName] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Assets_Symbol",
            table: "Assets",
            column: "Symbol",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ExchangeAssetConfigs_AssetId",
            table: "ExchangeAssetConfigs",
            column: "AssetId");

        migrationBuilder.CreateIndex(
            name: "IX_ExchangeAssetConfigs_ExchangeId_AssetId",
            table: "ExchangeAssetConfigs",
            columns: new[] { "ExchangeId", "AssetId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Exchanges_Name",
            table: "Exchanges",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FundingRateSnapshots_AssetId",
            table: "FundingRateSnapshots",
            column: "AssetId");

        migrationBuilder.CreateIndex(
            name: "IX_FundingRateSnapshots_ExchangeId_AssetId_RecordedAt",
            table: "FundingRateSnapshots",
            columns: new[] { "ExchangeId", "AssetId", "RecordedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Alerts");

        migrationBuilder.DropTable(
            name: "AspNetRoleClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserRoles");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "BotConfigurations");

        migrationBuilder.DropTable(
            name: "ExchangeAssetConfigs");

        migrationBuilder.DropTable(
            name: "FundingRateSnapshots");

        migrationBuilder.DropTable(
            name: "ArbitragePositions");

        migrationBuilder.DropTable(
            name: "AspNetRoles");

        migrationBuilder.DropTable(
            name: "AspNetUsers");

        migrationBuilder.DropTable(
            name: "Assets");

        migrationBuilder.DropTable(
            name: "Exchanges");
    }
}
