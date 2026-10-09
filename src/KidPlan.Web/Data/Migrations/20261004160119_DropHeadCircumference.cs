using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidPlan.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropHeadCircumference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeadCircumferenceCm",
                table: "GrowthRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HeadCircumferenceCm",
                table: "GrowthRecords",
                type: "decimal(4,1)",
                precision: 4,
                scale: 1,
                nullable: true);
        }
    }
}
