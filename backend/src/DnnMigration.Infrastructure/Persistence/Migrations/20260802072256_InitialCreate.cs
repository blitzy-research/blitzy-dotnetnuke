using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnnMigration.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Baseline migration that records the existing DotNetNuke schema without changing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This migration is <strong>intentionally empty</strong>, and it must stay that way. Its whole
    /// purpose is to write one row into the migrations history table so that Entity Framework
    /// considers the model to be already applied. Not one <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c>
    /// statement is emitted against a database by this migration or by anything else in this
    /// assembly.
    /// </para>
    /// <para>
    /// The reason is not caution, it is impossibility. The schema this model binds to is produced by
    /// an eighty-eight script upgrade chain whose terminal state depends on ASP.NET membership
    /// objects that those scripts only ever <c>ALTER</c>, never create. Two of them —
    /// <c>aspnet_Membership_UpdateUser</c> and <c>aspnet_Membership_UpdateUserInfo</c> — are
    /// Microsoft's own procedures that the chain patches to add failed-attempt and lockout
    /// bookkeeping; seventeen such object names are referenced in total, and every one of them is
    /// installed by an external registration tool. Replaying the chain against an empty database
    /// therefore cannot reproduce the terminal schema, and neither can any migration generated from
    /// this model. It follows that <c>Database.EnsureCreated</c> must never be called against a real
    /// database either.
    /// </para>
    /// <para>
    /// The generated body that Entity Framework produced here was removed deliberately rather than
    /// left in place unused. Leaving it would have created a real hazard: a routine
    /// <c>database update</c> against a populated DotNetNuke instance would attempt to create
    /// twenty-one tables that already exist and fail part way through, and against an empty database
    /// it would create tables that look right but lack the membership objects the schema depends on,
    /// producing a silently broken installation. An empty body cannot do either.
    /// </para>
    /// <para>
    /// The companion designer file and the model snapshot are <em>not</em> emptied. They record the
    /// shape of the model as of this baseline, which is what lets a genuinely additive future
    /// migration be scaffolded correctly: the scaffolder diffs against the snapshot, sees the
    /// twenty-one existing tables as already present, and emits only what actually changed.
    /// </para>
    /// <para>
    /// <c>Down</c> is empty for the same reason and for one more: the inverse of "record that the
    /// existing schema exists" is "stop recording it", which is exactly what removing the history row
    /// does. A generated <c>Down</c> would have dropped twenty-one tables of live tenant, account and
    /// permission data.
    /// </para>
    /// </remarks>
    internal partial class InitialCreate : Migration
    {
        /// <summary>
        /// Applies the baseline. Deliberately does nothing beyond recording the migration.
        /// </summary>
        /// <param name="migrationBuilder">The builder, intentionally unused.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. See the remarks on this class: the schema already exists and
            // depends on externally installed membership objects, so no schema statement may be
            // issued from here.
        }

        /// <summary>
        /// Reverts the baseline. Deliberately does nothing beyond removing the migration record.
        /// </summary>
        /// <param name="migrationBuilder">The builder, intentionally unused.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. Reverting a baseline means forgetting that the schema was
            // recorded, never destroying the schema itself.
        }
    }
}
