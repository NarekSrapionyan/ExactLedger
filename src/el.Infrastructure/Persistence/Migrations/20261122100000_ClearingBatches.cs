using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Hand-written: clearing tables are accessed with SQL and are not part of the EF model, so the model snapshot is unchanged.
    /// </summary>
    [DbContext(typeof(ExactLedgerDbContext))]
    [Migration("20261122100000_ClearingBatches")]
    public partial class ClearingBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ClearingSafetySql.Up);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ClearingSafetySql.Down);
    }
}
