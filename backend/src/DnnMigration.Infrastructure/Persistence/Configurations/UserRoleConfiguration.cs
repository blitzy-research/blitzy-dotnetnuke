using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

// MIGRATION: everything configured below is the CUMULATIVE TERMINAL state of the DotNetNuke 4.9
// upgrade chain, never the shape of any single script. Every statement that ever touched
// dbo.UserRoles was enumerated across all 88 scripts under
// Website/Providers/DataProviders/SqlDataProvider/ (UnInstall.SqlDataProvider excluded, which
// names only the surviving constraints and so corroborates rather than defines them). There are
// exactly fourteen ALTER TABLE statements against this table in the whole chain, and every one is
// accounted for below. The terminal column set is exactly UserRoleID, UserID, RoleID, ExpiryDate,
// IsTrialUsed and EffectiveDate - six columns, in that physical order, which is the order the
// properties are configured in.
//
// MIGRATION: THERE IS NO STATUS COLUMN ON THIS TABLE, and the absence is proved rather than
// assumed. Of those fourteen ALTER TABLE statements, twelve add or drop a key or foreign-key
// constraint - PK_UserRoles at 01.00.00:L441, the original constraint pair at 01.00.00:L688, then
// the FK_UserRoles_Roles cycle at 01.00.04:L1347 and :L1388, 01.00.05:L2773 and :L2811, and the
// FK_UserRoles_Users cycle at 01.00.05:L46 and :L84, 01.00.06:L223 and :L247, 03.00.13:L14 and
// :L55 - and the remaining two are the EffectiveDate addition at 03.02.03:L379 and its guarded
// twin at 04.00.04:L417. Not one of the fourteen adds a column other than EffectiveDate. The
// 03.00.09:L413-418 index rework is DROP INDEX and CREATE INDEX rather than ALTER TABLE and adds
// no column either.
//
// So the pending, active and expired classification of a membership is DERIVED, never stored. The
// domain entity computes it from EffectiveDate and ExpiryDate in UserRole.GetStatus(DateTime),
// which is a method: EF Core maps fields and properties, never methods, so nothing here needs to
// exclude it and nothing here may map it. Mapping, defaulting or inventing a status column would
// bind the model to an object no installation in the field has.
//
// MIGRATION: the legacy type this table's entity replaces was NOT a link-table object. UserRoleInfo
// declares "Inherits RoleInfo" at UserRoleInfo.vb:L42-43, so all fifteen of the role definition's
// own properties - RoleID, PortalID, RoleGroupID, RoleName, Description, BillingFrequency,
// ServiceFee, TrialFrequency, TrialPeriod, BillingPeriod, TrialFee, IsPublic, AutoAssignment,
// RSVPCode and IconFile - arrived on the assignment object as if the assignment owned them, and
// RoleController.vb:L494 reads one of them straight off the assignment ("userRole.ServiceFee > 0.0
// AndAlso userRole.IsTrialUsed"). Every one of those columns belongs to dbo.Roles and is mapped by
// RoleConfiguration alone. This file maps the six columns of dbo.UserRoles and nothing else; the
// role's own members are reached through the Role navigation, which is a foreign key rather than a
// base class. No discriminator column exists and no inheritance mapping is configured, because
// dbo.Roles and dbo.UserRoles are two tables in a foreign-key relationship and nothing more.
//
// Three legacy members are likewise absent because no column backs them: FullName and Email
// (UserRoleInfo.vb:L46-47) are projections of the joined dbo.Users row, and Subscribed
// (UserRoleInfo.vb:L51) is a correlated existence test computed by the role-listing procedures.
// All three belong to an Application-layer read model; the domain entity does not declare them, so
// there is nothing here to exclude.
//
// MIGRATION: Rule T4, schema immutability. This configuration binds to objects that already exist
// and describes nothing it may bring into being. It creates, alters and drops no table, index or
// constraint, declares no seed row and contains no literal SQL. The physical names of the primary
// key, both foreign keys and both indexes are stated explicitly so the mapping reads against a
// live DotNetNuke database rather than against a name EF Core would have invented.
//
// MIGRATION: Rule T7, sentinels. This table is the folder's sharpest sentinel risk, because both
// of its nullable columns are dates and the legacy Null.NullDate marker was Date.MinValue.
// RoleController.vb:L506 initialises an effective date to that marker and :L531 clears a past one
// back to it, so "unbounded" reached the legacy object as a magic value rather than as absence.
// The two properties are DateTime? and NO value conversion is installed in either direction: a
// null stays a null and is never written as, or read from, Date.MinValue. Two consequences of the
// identity seeds have to be stated rather than assumed. dbo.Roles.RoleID is declared
// IDENTITY(0, 1) at 01.00.00:L115, so zero is the FIRST REAL ROLE and never means "no role";
// dbo.Users.UserID is IDENTITY(1, 1) at 01.00.00:L98, so zero is never a real account. Neither
// key column ever carries the legacy Null.NullInteger marker of -1.

/// <summary>
/// Binds the <see cref="UserRole"/> entity to the existing <c>dbo.UserRoles</c> table - the row
/// that grants one account one role, for a bounded stretch of time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six columns, and the two that carry the assignment's meaning are dates rather than a state.</b>
/// A membership is pending, active or expired according to where the current instant falls between
/// <c>EffectiveDate</c> and <c>ExpiryDate</c>. No column records that classification, in this table
/// or in any other, so the mapping deliberately has nothing to say about it and the entity computes
/// it on demand.
/// </para>
/// <para>
/// <b>The key is the surrogate, and there is no uniqueness constraint on the pair.</b>
/// <c>PK_UserRoles</c> is clustered on <c>UserRoleID</c> alone, and an exhaustive search of the
/// upgrade chain finds no unique index or unique constraint over <c>(UserID, RoleID)</c>. Natural as
/// one looks on a link table, adding it would be an invention: it would reject a row a genuine
/// installation may already hold. This is the deliberate counterpart of <c>dbo.UserPortals</c>,
/// whose primary key IS the composite of its two foreign keys and whose identity column is
/// deliberately excluded from it - two link tables in the same schema, keyed two different ways.
/// </para>
/// <para>
/// <b>Both date columns are the legacy <c>datetime</c> type and must never be promoted.</b> EF Core
/// maps <see cref="DateTime"/> to the newer higher-precision type by default, which would round-trip
/// values this column cannot store and would compare unequal to what the database returns.
/// <c>datetime</c> resolves to roughly 3.33 milliseconds and its range begins in 1753.
/// </para>
/// <para>
/// <b><c>IsTrialUsed</c> is a nullable flag, so three states are representable</b> - the trial was
/// used, the trial was not used, and nothing is recorded. The mapping preserves all three rather
/// than collapsing the third into the second, which is a real behavioural difference from the legacy
/// code and is the reason the column carries neither a requiredness nor a default here.
/// </para>
/// <para>
/// <b>Both foreign keys cascade, and both are reproduced rather than softened.</b> Deleting an
/// account or a role withdraws its assignments, which is what lets role deletion leave no orphaned
/// membership behind without the service performing a second sweep. Two cascade paths converging on
/// one table is not a problem to design around: EF Core's model validator does not reject multiple
/// cascade paths, only the migration generator objects, and the baseline migration for this model is
/// intentionally empty under Rule T4.
/// </para>
/// <para>
/// The domain entity's <c>Identity</c> projection over <c>UserRoleID</c> is an equality concern and
/// carries no mapping authority; it is a get-only expression-bodied member with no setter and no
/// backing field, so EF Core's writability requirement excludes it naturally and it is deliberately
/// not suppressed here either - suppressing it would imply it had otherwise been mapped.
/// </para>
/// </remarks>
internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the role assignment entity type.</param>
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Website/release.config:L354 registers the SQL data provider with objectQualifier="" and
        // :L355 with databaseOwner="dbo", so the physical object is the unqualified UserRoles under
        // the dbo schema. Every physical name stated in this file follows from that pair. The table
        // name is genuinely plural.
        builder.ToTable("UserRoles", "dbo");

        // MIGRATION: the primary key is the SURROGATE and it is clustered. 01.00.00:L441-446 reads
        //     ALTER TABLE dbo.UserRoles WITH NOCHECK ADD
        //         CONSTRAINT PK_UserRoles PRIMARY KEY  CLUSTERED ( UserRoleID )  ON PRIMARY
        // and no script drops it; the 02.00.00 sp_rename calls that mention this table pass the
        // same name unchanged in both arguments and so are no-ops that prove nothing either way.
        //
        // There is NO unique constraint or unique index over (UserID, RoleID) anywhere in the
        // terminal schema - the only two indexes are single-column and non-unique, configured
        // below. Contrast dbo.UserPortals, whose primary key IS the composite of its two foreign
        // keys with its identity column deliberately excluded from it. The two link tables are keyed
        // differently, the difference is deliberate, and neither shape may be reasoned to by
        // analogy from the other.
        builder.HasKey(x => x.UserRoleId).HasName("PK_UserRoles");

        // MIGRATION: UserRoleID int IDENTITY (1, 1) NOT NULL at 01.00.00:L239. The seed is 1, the
        // ordinary case, unlike dbo.Roles and dbo.Tabs which seed at 0 and dbo.Portals which seeds
        // at -1. ValueGeneratedOnAdd keeps EF Core from inserting into an identity column and makes
        // it read the generated value back; UseIdentityColumn records the seed and increment the
        // schema actually declares, so the model stays an honest description of the database.
        builder.Property(x => x.UserRoleId)
            .HasColumnName("UserRoleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: upper-case ID, and the casing is per table rather than per schema. This table
        // declares UserID int NOT NULL at 01.00.00:L240 and RoleID int NOT NULL at :L241, while
        // dbo.UserPortals spells the very same two ideas UserId and PortalId with a lower-case d.
        // Each HasColumnName carries the spelling of the table being configured, never a normalised
        // one, so neither may be copied across from the other.
        builder.Property(x => x.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(x => x.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: ExpiryDate datetime NULL at 01.00.00:L242, present since the baseline. The type
        // is stated explicitly because EF Core's default for DateTime? is the newer
        // higher-precision type, which this column is not.
        //
        // MIGRATION: Rule T7. Nullable in the schema and DateTime? in the model, with NO conversion
        // between null and the legacy Null.NullDate marker of Date.MinValue in either direction -
        // even though RoleController.vb carried absence as exactly that marker. A null here means
        // the membership never lapses, which is what the terminal GetRolesByUser predicate tests
        // for when it admits a row whose ExpiryDate is null or is at or after the current instant.
        builder.Property(x => x.ExpiryDate)
            .HasColumnName("ExpiryDate")
            .HasColumnType("datetime");

        // MIGRATION: IsTrialUsed bit NULL at 01.00.00:L243 - nullable, and deliberately mapped
        // bool? with NEITHER IsRequired NOR a default value. The nullability is a genuine
        // tri-state: the trial was used, the trial was not used, and nothing was ever recorded.
        // The legacy property was a non-nullable Boolean and Null.NullBoolean is false, so
        // hydration silently collapsed the third state into the second and RoleController.vb:L494
        // then branched on the collapsed value. Restoring the third state is the point.
        //
        // This deliberately differs from dbo.Roles.IsPublic and dbo.Roles.AutoAssignment, which are
        // bit NOT NULL with a store default of 0 and are configured accordingly in
        // RoleConfiguration. Adding IsRequired here would reject rows a live installation holds,
        // and adding a default would make EF Core treat the property as store-generated and omit a
        // deliberate false from the insert.
        builder.Property(x => x.IsTrialUsed)
            .HasColumnName("IsTrialUsed")
            .HasColumnType("bit");

        // MIGRATION: EffectiveDate is the SIXTH AND LAST column, added late in the chain rather
        // than created with the table, which is why it is configured last: the order here is the
        // physical column order of the terminal table. 03.02.03:L379-380 reads
        //     ALTER TABLE UserRoles
        //         ADD EffectiveDate datetime NULL
        // and 04.00.04:L415-419 repeats it verbatim inside an "If (SELECT fn_GetVersion(3,2,3)) = 0"
        // guard, so an installation that already ran 03.02.03 does not add it twice. The pair is ONE
        // logical change and a second, guarded ADD is not a seventh column.
        //
        // Legacy datetime and nullable, with the same Rule T7 treatment as ExpiryDate above and no
        // sentinel conversion: a null means the membership counts immediately, matching the
        // inclusive effective-date half of the terminal GetRolesByUser predicate.
        builder.Property(x => x.EffectiveDate)
            .HasColumnName("EffectiveDate")
            .HasColumnType("datetime");

        // MIGRATION: the two index names are CROSSED relative to the columns they cover, and the
        // pairing is easy to reverse by accident. 01.00.10:L747-748 and :L755-756 created the pair,
        // 03.00.09:L413 and :L415 dropped it, and :L417-418 recreated them in their terminal form:
        //     CREATE NONCLUSTERED INDEX IX_UserRoles   ON UserRoles (RoleID)
        //     CREATE NONCLUSTERED INDEX IX_UserRoles_1 ON UserRoles (UserID)
        // So the unsuffixed name covers RoleID and the _1 suffix covers UserID. dbo.UserPortals is
        // reworked in the same 03.00.09 block, three lines earlier, with its unsuffixed name over
        // the PORTAL side - so the two link tables are not symmetrical and the pairing must be read
        // off the citations rather than inferred from the neighbour.
        //
        // NEITHER index is unique. Marking either one unique would forbid a role from having a
        // second member, or an account from holding a second role. The physical names are preserved
        // because the object qualifier is empty.
        builder.HasIndex(x => x.RoleId).HasDatabaseName("IX_UserRoles");
        builder.HasIndex(x => x.UserId).HasDatabaseName("IX_UserRoles_1");

        // MIGRATION: both terminal foreign keys declare ON DELETE CASCADE and both are reproduced
        // faithfully. Each was dropped and re-added several times, not because its semantics
        // changed but because the table it points AT was rebuilt through a Tmp_ table, so only the
        // LAST re-add in script order is terminal.
        //
        // FK_UserRoles_Roles reached its terminal form at 01.00.05:L2811-2820 -
        //     ALTER TABLE dbo.UserRoles WITH NOCHECK ADD CONSTRAINT FK_UserRoles_Roles
        //         FOREIGN KEY ( RoleID ) REFERENCES dbo.Roles ( RoleID )
        //         ON DELETE CASCADE NOT FOR REPLICATION
        // after 01.00.05:L2773 dropped it so that 01.00.05:L2777 could DROP TABLE dbo.Roles and
        // :L2780 rename Tmp_Roles into its place; the earlier and identical versions at
        // 01.00.00:L689-694 and 01.00.04:L1388-1396 are superseded.
        //
        // FK_UserRoles_Users reached its terminal form at 03.00.13:L55-63 in the same shape against
        // Users (UserID), after the versions at 01.00.00:L695-700, 01.00.05:L84-92 and
        // 01.00.06:L247-256 were each dropped for a Users rebuild.
        //
        // MIGRATION: this is a REAL, enforced, cascading foreign key to dbo.Roles, and that must not
        // be generalised in either direction. dbo.ModulePermission and dbo.TabPermission also carry
        // a RoleID, but the terminal schema declares no physical foreign key from either of them
        // back to dbo.Roles, so both are configured to take no action. Each table is measured on its
        // own DDL. Two cascade paths converge here - Portals to Roles to UserRoles, and Users to
        // UserRoles - and neither is downgraded to quiet a warning that is never raised: EF Core's
        // model validator does not reject multiple cascade paths, only migration generation objects,
        // and the baseline migration is intentionally inert under Rule T4. Softening either end
        // would leave behind membership rows the database itself removes.
        //
        // WITH NOCHECK and NOT FOR REPLICATION have no EF Core equivalent and are deliberately
        // unrepresented, here and throughout this folder.
        //
        // Both relationships are owned HERE, on the dependent, and configured exactly once.
        // RoleConfiguration and UserConfiguration deliberately declare neither: applying
        // configurations from an assembly gives no ordering guarantee, so a relationship written
        // from both ends would take whichever delete behaviour happened to run last, with nothing in
        // the build and nothing in model validation to say so. No IsRequired is stated on either
        // relationship - both foreign-key properties are non-nullable int, from which EF Core
        // already derives that both principals are required.
        builder.HasOne(x => x.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .HasConstraintName("FK_UserRoles_Roles")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("FK_UserRoles_Users")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
