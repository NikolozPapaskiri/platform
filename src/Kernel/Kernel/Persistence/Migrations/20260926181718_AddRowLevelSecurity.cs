using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Kernel.Persistence.Migrations
{
    /// <summary>
    /// PostgreSQL row-level security for every tenant-owned table.
    /// HAND-WRITE: Nika. Requirements and the contract tests to pass: docs/hand-write/m0-tenancy.md.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TODO(nika)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // TODO(nika)
        }
    }
}
