using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriSoftic.GrowthEngine.Modules.Content.Migrations
{
    /// <inheritdoc />
    public partial class VersionMediaDataFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows that existed before files could be attached got a placeholder object; they carry no files.
            migrationBuilder.Sql("UPDATE content.versions SET \"AssetIdsJson\" = '[]'::jsonb WHERE jsonb_typeof(\"AssetIdsJson\") <> 'array'");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
