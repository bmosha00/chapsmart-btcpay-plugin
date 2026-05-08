using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ChapSmart.Data.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "BTCPayServer.Plugins.ChapSmart");

            migrationBuilder.CreateTable(
                name: "Payouts",
                schema: "BTCPayServer.Plugins.ChapSmart",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    RecipientName = table.Column<string>(type: "text", nullable: true),
                    AmountTZS = table.Column<decimal>(type: "numeric", nullable: false),
                    AmountBTC = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PaymentProviderTransId = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ResponseData = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_InvoiceId",
                schema: "BTCPayServer.Plugins.ChapSmart",
                table: "Payouts",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_StoreId",
                schema: "BTCPayServer.Plugins.ChapSmart",
                table: "Payouts",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_Status",
                schema: "BTCPayServer.Plugins.ChapSmart",
                table: "Payouts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_CreatedAt",
                schema: "BTCPayServer.Plugins.ChapSmart",
                table: "Payouts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Payouts",
                schema: "BTCPayServer.Plugins.ChapSmart");
        }
    }
}
