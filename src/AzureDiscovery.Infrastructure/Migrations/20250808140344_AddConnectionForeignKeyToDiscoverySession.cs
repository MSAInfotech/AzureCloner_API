using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionForeignKeyToDiscoverySession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConnectionId",
                table: "DiscoverySessions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverySessions_ConnectionId",
                table: "DiscoverySessions",
                column: "ConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_DiscoverySessions_AzureConnections_ConnectionId",
                table: "DiscoverySessions",
                column: "ConnectionId",
                principalTable: "AzureConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DiscoverySessions_AzureConnections_ConnectionId",
                table: "DiscoverySessions");

            migrationBuilder.DropIndex(
                name: "IX_DiscoverySessions_ConnectionId",
                table: "DiscoverySessions");

            migrationBuilder.DropColumn(
                name: "ConnectionId",
                table: "DiscoverySessions");
        }
    }
}
