using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DisputePortal.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDisputesLatestByTransactionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Disputes_TransactionId_CreatedAt_Id",
                table: "Disputes",
                columns: new[] { "TransactionId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Disputes_TransactionId_CreatedAt_Id",
                table: "Disputes");
        }
    }
}
