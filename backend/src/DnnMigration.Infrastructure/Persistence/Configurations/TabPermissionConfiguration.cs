using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="TabPermission"/> to the existing, immutable DotNetNuke 4.9 SQL Server table
/// <c>dbo.TabPermission</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table name is singular.</b> The terminal schema declares <c>TabPermission</c>, so the name is
/// stated explicitly and the provider's pluralising convention is deliberately overridden. The plural
/// spelling exists in this database only as the name of a legacy read view, never as a table, and the
/// mistake would surface solely as a runtime failure on the first query rather than at build time.
/// </para>
/// <para>
/// <b>This is a grant, not a permission.</b> A row records that one subject has been allowed or refused
/// one catalogue entry on one page. What a permission <i>is</i> lives in <c>dbo.Permission</c> and is
/// mapped by <c>PermissionConfiguration</c>, which is why no catalogue column appears anywhere here.
/// </para>
/// <para>
/// <b>Both subject columns are nullable, and one of them tells a destructive upgrade story.</b>
/// <c>RoleID</c> was created <c>NOT NULL</c> and was later rebuilt as <c>NULL</c> through a temporary
/// staging column, while <c>UserID</c> was added nullable long after the table existed. A terminal row
/// therefore addresses a role, or an account, or neither. Reading only the original <c>CREATE TABLE</c>
/// gives the wrong nullability for <c>RoleID</c> and would reject rows the legacy application writes
/// routinely; the provenance is recorded at the property itself.
/// </para>
/// <para>
/// <b>A negative role identifier is real data, not an absent value.</b> <c>-1</c>, <c>-2</c> and
/// <c>-3</c> are persisted pseudo-principals that match no <c>dbo.Roles</c> row. Absence is expressed by
/// SQL <c>NULL</c> and by nothing else, so no value conversion between the two ever appears here. The
/// full argument, and the reason the schema declines to enforce a role foreign key because of it, is
/// recorded at the role relationship.
/// </para>
/// <para>
/// <b>Two of the four relationships cascade in the database and two enforce nothing.</b> The page and
/// catalogue references carry real <c>ON DELETE CASCADE</c> foreign keys and are mapped as such. The
/// account reference has a real foreign key with no <c>ON DELETE</c> clause, and the role reference has
/// no foreign key at all; both are therefore mapped so that the model deletes nothing, matching what the
/// database actually enforces rather than what a convention would assume.
/// </para>
/// <para>
/// <b>There are five indexes, and only the first is unique.</b> <c>IX_TabPermission</c> spans the page,
/// the catalogue entry and both subject columns. Because this engine treats nulls as equal for
/// uniqueness, that admits exactly one subject-less grant per page and permission pair while still
/// distinguishing a role-targeted row from an account-targeted one. The remaining four are single-column
/// lookup indexes whose names follow the <i>referenced</i> table and are consequently inconsistent in
/// their pluralisation; each is reproduced verbatim.
/// </para>
/// <para>
/// <b>Scope.</b> This type maps storage and nothing else. Permission <i>evaluation</i> - which of an
/// allow and a refusal wins, and what an absent grant means - lives in
/// <c>Infrastructure/Security/PermissionEvaluator.cs</c> and the authorisation policies that consume it.
/// No evaluation, query, service or dependency-registration logic appears here.
/// </para>
/// </remarks>
internal sealed class TabPermissionConfiguration : IEntityTypeConfiguration<TabPermission>
{
    /// <summary>
    /// Applies the mapping for the page grant entity type.
    /// </summary>
    /// <param name="builder">The builder for the page grant entity type.</param>
    public void Configure(EntityTypeBuilder<TabPermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: how the citations below were obtained, recorded so they can be re-checked rather
        //   than trusted. The 88 upgrade scripts under
        //   Website/Providers/DataProviders/SqlDataProvider/ are CRLF-terminated and template their
        //   object names as {databaseOwner}{objectQualifier}Name, so a naive search reports an object as
        //   absent when it is present. Every line reference here was taken after stripping the carriage
        //   returns, the two template tokens and the square brackets, searching case-insensitively, and
        //   excluding UnInstall.SqlDataProvider - 87 scripts.
        //
        //   Three hazards bite on this table specifically and each produced a false result before it was
        //   accounted for. First, the keyword CONSTRAINT frequently ends one line with the constraint
        //   name on the next - true of the two key drops at 03.00.09:L453-L454 and L456-L457 and of the
        //   account key at 04.05.00:L485-L486 - so a single-line search finds none of them. Second,
        //   legacy constraint naming is inconsistent between the two sibling grant tables, so a pattern
        //   anchored on an assumed separator yields false absences; the note at the account
        //   relationship below records the measured difference. Third, the four lookup indexes this
        //   table owns sit immediately BEFORE the module grant table's four at 04.06.00:L1207-L1230, so
        //   an off-by-one block read attributes one table's indexes to the other; the two blocks were
        //   read separately and confirmed distinct.
        //
        //   A fourth hazard is subtler and is why the absence proof further down is worded as it is: the
        //   normalising search strips the {databaseOwner} TOKEN but NOT a literal dbo. prefix, and the
        //   role keys in this schema are written REFERENCES dbo.Roles - so a sweep anchored on
        //   "REFERENCES Roles" returns zero hits and appears to prove an absence it has not tested.
        //
        // MIGRATION: the legacy inheritance is deliberately gone, and it is the reason this file maps
        //   six columns rather than eleven. Library/Components/Security/Permissions/TabPermission.vb
        //   L29-L30 declares TabPermissionInfo Inherits PermissionInfo, so one legacy object surfaced
        //   both this table's assignment columns and every column of the catalogue entry it merely
        //   referred to. The terminal table stores none of those catalogue columns. This configuration
        //   therefore splits on the physical table boundary: all five columns of dbo.Permission are
        //   mapped only by PermissionConfiguration and are reached from here through the Permission
        //   navigation. The legacy carrier also cached three joined display values for the role, the
        //   account and its friendly name (TabPermission.vb L38, L40 and L41); none is a column of this
        //   table either - they are projections of the read view discussed below - so none appears here.
        //
        // MIGRATION: Rule T8 - legacy machinery that deliberately produces nothing here. The
        //   pre-generics TabPermissionCollection wrapper yields no target type; uniqueness is the
        //   database's four-column index configured at the end of this method, and identity equality
        //   comes from Entity<int>. The reflection-based row hydrator and the sentinel-translation
        //   helpers in Library/Components/Shared/Null.vb are likewise replaced by the provider's own
        //   materialiser, so no hydration contract is reproduced.
        //
        // MIGRATION: Rule T4 - machinery that is deliberately NOT reproduced. The plural-named read
        //   view over this table, dropped and recreated at 04.05.00:L499-L530, is not mapped: no
        //   keyless type and no view mapping appears here. The legacy INSERT rows are not reproduced as
        //   model seed data - this configuration maps an existing table, it never writes to one. The
        //   two permission tables that are beyond this folder's scope share DDL statements with this
        //   one across the chain and are named nowhere in this file. And no schema-altering or
        //   raw-SQL construct of any kind appears, because the baseline migration is intentionally
        //   empty and this mapping must be applicable to a live DotNetNuke database as a no-op.

        // MIGRATION: SINGULAR legacy table name. CREATE TABLE TabPermission is declared once in the
        //   whole chain, at 02.02.00:L693, and the provider's pluralising convention would look for a
        //   plural table that does not exist. Three of the four tables in this family are singular, so
        //   the name is asserted per table rather than assumed folder-wide. Website/release.config:L354
        //   registers objectQualifier as empty and :L355 registers databaseOwner as dbo, which is why
        //   the name is unprefixed and the schema is dbo.
        builder.ToTable("TabPermission", "dbo");

        // MIGRATION: primary-key lineage. Added as PK_TabPermission PRIMARY KEY CLUSTERED over
        //   TabPermissionID at 02.02.00:L730-L734, dropped at 03.00.09:L465 and recreated clustered at
        //   03.00.09:L473 - the terminal form. The constraint is named explicitly so the model carries
        //   the name the database already has rather than one invented by convention. The key is the
        //   row's, not the subject's: which grants may coexist is the separate four-column unique index
        //   configured further down, and the two are independent.
        builder.HasKey(g => g.TabPermissionId)
            .HasName("PK_TabPermission");

        // MIGRATION: TabPermissionID int IDENTITY (1, 1) NOT NULL at 02.02.00:L694. The column keeps
        //   the legacy upper-case ID spelling. The seed is recorded so a model-generated script would
        //   continue the existing sequence rather than restart it. A seed of 1 is the ordinary case and
        //   means no valid key on this table collides with the legacy absent-integer marker of -1 - but
        //   that local good fortune is emphatically not shared by the columns below: Tabs.TabID and
        //   Roles.RoleID are both IDENTITY (0, 1) at 01.00.00:L140 and 01.00.00:L115, so 0 is the first
        //   real page and the first real role, and nothing in this model may read 0 as "not saved yet".
        builder.Property(g => g.TabPermissionId)
            .HasColumnName("TabPermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: TabID int NOT NULL at 02.02.00:L695, never re-typed by any later script. Required,
        //   and backed by a real cascading foreign key configured below. Indexed at
        //   04.06.00:L1187-L1191. Legitimately 0, because the page table's key seeds at 0.
        builder.Property(g => g.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: PermissionID int NOT NULL at 02.02.00:L696, never re-typed. Required, and backed by
        //   a real cascading foreign key configured below. This column is what REPLACES the legacy
        //   inheritance described at the top of this method: a grant names its catalogue entry and
        //   carries no copy of it. Indexed at 04.06.00:L1181-L1185.
        builder.Property(g => g.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: RoleID is NULLABLE in the terminal schema, and the provenance matters because the
        //   baseline declaration says otherwise. It was created int NOT NULL at 02.02.00:L697.
        //   04.05.00:L456-L478 then rebuilt it destructively through a temporary staging column: ADD a
        //   staging column int NULL (L456-L457), UPDATE it from RoleID (L460-L461), DROP COLUMN RoleID
        //   (L464-L465), ADD RoleID int NULL (L468-L469), UPDATE RoleID back from the staging column
        //   (L472-L473), then DROP COLUMN on the staging column (L476-L477). Reading only the CREATE
        //   TABLE yields the wrong nullability and a mapping that rejects rows the legacy application
        //   writes routinely, so this property is int? and is deliberately not marked as required. The
        //   staging column is dropped inside that same block, survives in no terminal database, and is
        //   never mapped. Re-adding the column after the drop is also why it carries no foreign key.
        //   Indexed at 04.06.00:L1199-L1203.
        //
        // MIGRATION: Rule T7 - this column carries the most dangerous sentinel collision in the folder,
        //   and no conversion may ever be installed on it. NEGATIVE values here are REAL, PERSISTED
        //   pseudo-principals, not markers for an absent value: -1 is All Users, -2 is Superuser and -3
        //   is Unauthenticated Users, per the constants at Library/Components/Shared/Globals.vb
        //   L95-L97. The schema itself says so - the read view recreated at 04.05.00:L503-L530 resolves
        //   the role's display name with CASE TP.RoleID WHEN -1 THEN 'All Users' WHEN -2 THEN
        //   'Superuser' WHEN -3 THEN 'Unauthenticated Users' ELSE R.<name> END, and it reaches the role
        //   table through a LEFT OUTER JOIN precisely because those three values match no row there.
        //   So a RoleID of -1 means "the All Users pseudo-role", NOT "absent". Absence is expressed
        //   ONLY by SQL NULL. Collapsing the two in either direction - null to -1, or -1 to null -
        //   would silently grant or revoke page access to every visitor of every portal, which is why
        //   no value converter, no database default and no clamping appears on this property. A fourth
        //   constant, glbRoleNothing = -4 (Globals.vb L98), was the legacy in-memory "no role chosen"
        //   marker assigned by the constructor at TabPermission.vb L48; null expresses that here and -4
        //   is not expected in a row. The value round-trips exactly as stored.
        builder.Property(g => g.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int");

        // MIGRATION: UserID did not exist in the original table. 04.05.00:L482-L483 adds it as int NULL,
        //   guarded by IF (SELECT COLUMNPROPERTY(OBJECT_ID('TabPermission'), 'UserID', 'AllowsNull')) IS
        //   Null at L480 so the script is re-runnable - which makes the column look conditional in the
        //   script while being unconditionally present in any 4.9 database. Its arrival is what makes a
        //   grant to a single account possible, so a terminal row targets either a role or an account.
        //   Nullable, therefore not marked as required. Indexed at 04.06.00:L1193-L1197.
        //
        // MIGRATION: Rule T7 again, in the opposite direction to RoleID. The legacy code distinguished a
        //   role grant from an account grant by testing this column against the -1 absent-integer
        //   marker, so here -1 genuinely meant "no account" rather than "account number minus one".
        //   That test becomes a plain null check and -1 is never written to this column. The asymmetry
        //   with RoleID is deliberate and is the whole reason both notes exist: the same integer means
        //   "absent" on one column and "a real principal" on the other.
        builder.Property(g => g.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int");

        // MIGRATION: AllowAccess bit NOT NULL at 02.02.00:L698, declared WITHOUT a database default -
        //   unlike several bit columns on Modules, Tabs and TabModules - which is why no default value
        //   appears anywhere in this file. Required.
        //
        // MIGRATION: Rule T2 - this flag states what ONE row says and this configuration draws no
        //   conclusion from it. How a set of rows combines into an answer, and which of an allow and a
        //   refusal wins, is an evaluation rule belonging to
        //   Infrastructure/Security/PermissionEvaluator.cs. The distinction is load-bearing because a
        //   precedence rule restated here could contradict the component that actually applies it.
        builder.Property(g => g.AllowAccess)
            .HasColumnName("AllowAccess")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: this file declares FOUR relationships and it declares every one of them from the
        //   DEPENDENT end. The assembly scan that applies these configurations guarantees no ordering,
        //   so a relationship declared from both ends would let whichever end ran last silently decide
        //   the delete behaviour, with neither a compile error nor a model-validation error to reveal
        //   it. TabConfiguration, PermissionConfiguration, RoleConfiguration and UserConfiguration all
        //   deliberately decline to map their TabPermissions inverse navigations for that reason, so no
        //   principal-side mapping and no HasMany appears here either. Requiredness is derived from
        //   property nullability - TabId and PermissionId are int, so those two are required; RoleId and
        //   UserId are int?, so those two are optional - and requiredness is never restated on a
        //   relationship builder.
        //
        // MIGRATION: NOT FOR REPLICATION and WITH NOCHECK, which several of these constraints carry in
        //   the scripts, have no counterpart in this API and are deliberately unrepresented across
        //   this folder.

        // MIGRATION: FK_TabPermission_Tabs FOREIGN KEY (TabID) REFERENCES Tabs (TabID) ON DELETE
        //   CASCADE. Declared at 02.02.00:L783-L788, dropped at 03.00.09:L456-L457 and re-added at
        //   03.00.09:L487 and L489 - the terminal form, added in the same single statement as the
        //   catalogue key below. The cascade is real, so deleting a page withdraws its grants in the
        //   database whatever this model says, and the mapping agrees with it. It is NOT downgraded to
        //   avoid a multiple cascade path: the model validator does not reject multiple cascade paths,
        //   only the migration generator complains, and the baseline migration is intentionally empty
        //   per Rule T4. The constraint name is attached because the database already has it.
        builder.HasOne(g => g.Tab)
            .WithMany(t => t.TabPermissions)
            .HasForeignKey(g => g.TabId)
            .HasConstraintName("FK_TabPermission_Tabs")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: FK_TabPermission_Permission FOREIGN KEY (PermissionID) REFERENCES Permission
        //   (PermissionID) ON DELETE CASCADE. Declared at 02.02.00:L777-L782, dropped at
        //   03.00.09:L453-L454 and re-added at 03.00.09:L487-L488 - the terminal form. Retiring a
        //   catalogue entry therefore withdraws the grants that named it. Reproduced faithfully as a
        //   cascade for the same reasons as the page key above.
        builder.HasOne(g => g.Permission)
            .WithMany(p => p.TabPermissions)
            .HasForeignKey(g => g.PermissionId)
            .HasConstraintName("FK_TabPermission_Permission")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: FOREIGN-KEY ABSENCE PROOF. There is NO physical foreign key from this table to
        //   Roles anywhere in the terminal schema, established two independent ways over all 87
        //   non-UnInstall scripts using the normalising search described at the top of this method.
        //   First, a census of every constraint name containing "Permission" returns NINE foreign keys
        //   and no others; the three owned by this table are FK_TabPermission_Tabs,
        //   FK_TabPermission_Permission and FK_TabPermission_Users, and not one constraint in the whole
        //   census references Roles under any spelling. A targeted sweep for a name containing both
        //   "Permission" and "Role" returns only the three single-column lookup INDEXES named for the
        //   role column, never a key. Second, a sweep of every foreign key referencing the Roles table -
        //   written REFERENCES dbo.Roles, so the sweep allows for the qualifier and flattens newlines -
        //   returns exactly one constraint, FK_UserRoles_Roles, declared at 01.00.00:L689-L694 and
        //   recreated at 01.00.04:L1389 and 01.00.05:L2812. All three are owned by UserRoles, none by a
        //   permission table.
        //
        // MIGRATION: the key is absent BY DESIGN, not by omission, and the reason is the sentinel note
        //   on RoleId above: the persisted pseudo-principals -1, -2 and -3 match no Roles row, so a real
        //   key would reject the very rows a live DotNetNuke database contains. Three consequences are
        //   deliberate. The relationship is declared here anyway - omitting it would let convention
        //   infer the key and apply its own default of clearing the foreign key on the client, which the
        //   schema does not do - so declaring it explicitly is what pins the behaviour. It is mapped so
        //   that the model deletes nothing, because the database enforces and cascades nothing here and
        //   a pseudo-principal has no principal row to cascade from. And NO constraint name is attached,
        //   precisely because there is no constraint to name. The practical caveat for callers is that
        //   this navigation may be null even when RoleId holds a value, so no query may rely on being
        //   able to include it; read RoleId for the fact.
        //
        // MIGRATION: do not generalise this finding in either direction. UserRole DOES have a real
        //   cascading foreign key to Roles - FK_UserRoles_Roles ... ON DELETE CASCADE at
        //   01.00.05:L2811-L2819 - while the permission tables have none. Each was measured
        //   independently, and a sibling configuration copying either shape onto the other would
        //   contradict the schema.
        builder.HasOne(g => g.Role)
            .WithMany(r => r.TabPermissions)
            .HasForeignKey(g => g.RoleId)
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: the account key on THIS table is the conventionally named one, and that is itself
        //   the finding. It is FK_TabPermission_Users - WITH the separator - declared at
        //   04.05.00:L485-L492, whereas the module grant twin is FK_ModulePermissionUsers with none at
        //   04.05.00:L645. The third table in the family shares the module twin's quirk, which makes
        //   THIS table the exception rather than that one. A search anchored on either style is
        //   therefore not trustworthy as an absence proof, and this note exists so nobody re-derives a
        //   wrong conclusion from one.
        //
        // MIGRATION: the constraint is declared REFERENCES Users (UserID) NOT FOR REPLICATION with NO
        //   ON DELETE clause at 04.05.00:L489-L492, so the terminal behaviour deletes nothing and the
        //   mapping says so rather than accepting a convention default. Unlike the role reference above
        //   this one is genuinely enforced, so a non-null UserId does name an existing account. The real
        //   constraint name is attached so the model carries what the database already has.
        builder.HasOne(g => g.User)
            .WithMany(u => u.TabPermissions)
            .HasForeignKey(g => g.UserId)
            .HasConstraintName("FK_TabPermission_Users")
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: the terminal object set for this table is exactly ten objects, and every one is
        //   accounted for in this file. An exhaustive sweep of every CREATE TABLE, ALTER TABLE and
        //   CREATE INDEX statement naming this table returns nineteen statements across five scripts -
        //   02.02.00, 03.00.09, 04.05.00, 04.05.02 and 04.06.00, with nothing after 04.06.00 touching
        //   it - which resolve to: PK_TabPermission, the unique index below, the four lookup indexes
        //   below, FK_TabPermission_Tabs, FK_TabPermission_Permission, FK_TabPermission_Users, and the
        //   six columns. There is no foreign key to Roles, no column default, no check constraint and no
        //   surviving staging column.
        //
        // MIGRATION: declared as a UNIQUE NONCLUSTERED table CONSTRAINT rather than as a standalone
        //   unique index - ALTER TABLE TabPermission ADD CONSTRAINT IX_TabPermission UNIQUE
        //   NONCLUSTERED (TabID, PermissionID, RoleID, UserID) ON PRIMARY at 04.05.02:L214-L221 - and
        //   the provider expresses both forms identically as a unique index. Never dropped. The column
        //   order is reproduced exactly. Both trailing columns are nullable and this engine treats nulls
        //   as equal for uniqueness, so a role-targeted row and an account-targeted row remain
        //   distinguished while exactly one subject-less grant is admitted per page and permission pair.
        //   The 04.05.02 script rewrites and de-duplicates rows immediately before adding the
        //   constraint - including rewriting a role identifier of -1 to NULL on account-scoped rows at
        //   L144-L180 - direct evidence that live installations held duplicates and that both subject
        //   columns are genuinely nullable, so this rule is load-bearing rather than decorative. The
        //   database is the enforcer; no check in this layer duplicates it.
        //
        // MIGRATION: THE FILTER IS EXPLICITLY SUPPRESSED, and on this table it is the difference
        //   between a constraint that works and one that does not. The SQL Server provider attaches a
        //   "RoleID IS NOT NULL AND UserID IS NOT NULL" predicate to a unique index over nullable
        //   columns unless told otherwise. Every real grant targets EITHER a role OR an account, never
        //   both, so exactly one of those columns is null on essentially every row - which means the
        //   generated filter would exclude essentially every row from uniqueness enforcement and permit
        //   the duplicates the 04.05.02 de-duplication pass was written to remove. The terminal object
        //   is a plain UNIQUE NONCLUSTERED table constraint with no predicate; passing null as the
        //   filter removes the predicate and restores the declared rule.
        builder.HasIndex(g => new { g.TabId, g.PermissionId, g.RoleId, g.UserId })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_TabPermission");

        // MIGRATION: four single-column lookup indexes, one per foreign-key column plus one for the
        //   unenforced role column, added at 04.06.00:L1181-L1203 under IF NOT EXISTS (SELECT * FROM
        //   dbo.sysindexes WHERE name = N'...') guards, beneath the comment at L1180. All four are
        //   NON-unique; only the four-column constraint above is unique.
        //
        //   Their names follow the REFERENCED table rather than any consistent rule, which is why the
        //   suffixes disagree on pluralisation: _Permission is singular because dbo.Permission is,
        //   while _Tabs, _Users and _Roles are plural because those tables are. Each is carried verbatim
        //   and none is normalised - renaming one would make the model describe an index the database
        //   does not have.
        //
        //   These four sit immediately BEFORE the module grant table's own four at 04.06.00:L1207-L1230,
        //   and the two blocks are near-identical in shape. They were read separately and confirmed
        //   distinct; conflating them would silently attribute one table's indexes to the other.
        builder.HasIndex(g => g.PermissionId)
            .HasDatabaseName("IX_TabPermission_Permission");

        builder.HasIndex(g => g.TabId)
            .HasDatabaseName("IX_TabPermission_Tabs");

        builder.HasIndex(g => g.UserId)
            .HasDatabaseName("IX_TabPermission_Users");

        builder.HasIndex(g => g.RoleId)
            .HasDatabaseName("IX_TabPermission_Roles");
    }
}
