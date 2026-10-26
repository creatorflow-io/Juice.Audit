using Juice.EF;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Audit.EF.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantId : Migration
    {
        private readonly string _schema = "App";
        public AddTenantId()
        {
        }

        public AddTenantId(ISchemaDbContext schema)
        {
            _schema = schema.Schema;
        }
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: _schema,
                table: "AccessLog",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: _schema,
                table: "DataAudit",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessLog_TenantId_DateTime",
                schema: _schema,
                table: "AccessLog",
                columns: new[] { "TenantId", "DateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_DataAudit_TenantId_DateTime",
                schema: _schema,
                table: "DataAudit",
                columns: new[] { "TenantId", "DateTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccessLog_TenantId_DateTime",
                schema: _schema,
                table: "AccessLog");

            migrationBuilder.DropIndex(
                name: "IX_DataAudit_TenantId_DateTime",
                schema: _schema,
                table: "DataAudit");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: _schema,
                table: "AccessLog");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: _schema,
                table: "DataAudit");
        }
    }
}
