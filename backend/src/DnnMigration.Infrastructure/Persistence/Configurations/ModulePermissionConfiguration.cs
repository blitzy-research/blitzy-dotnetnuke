using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="ModulePermission"/> to the existing, immutable DotNetNuke 4.9 SQL Server table
/// <c>dbo.ModulePermission</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table name is singular.</b> The terminal schema declares <c>ModulePermission</c>, so the
/// mapping states the name explicitly and the provider's pluralising convention is deliberately
/// overridden. <c>ModulePermissions</c> does not exist in any database this model is pointed at, and
/// the mistake would surface only as a runtime failure on first query.
/// </para>
/// <para>
/// <b>This is a grant, not a permission.</b> A row records that one subject has been allowed or
/// refused one catalogue entry on one module instance. What a permission <i>is</i> lives in
/// <c>dbo.Permission</c> and is mapped by <c>PermissionConfiguration</c>, which is why no permission
/// code, key or name appears anywhere in this file.
/// </para>
/// <para>
/// <b>Both subject columns are nullable, and one of them tells a destructive migration story.</b>
/// <c>RoleID</c> was created <c>NOT NULL</c> and was later rebuilt as <c>NULL</c> through a temporary
/// column, while <c>UserID</c> was added nullable long after the table existed. A terminal row
/// therefore addresses a role, or an account, or neither. Reading only the original
/// <c>CREATE TABLE</c> gives the wrong nullability for <c>RoleID</c> and would reject rows the legacy
/// application writes routinely; the provenance is recorded at the property itself.
/// </para>
/// <para>
/// <b>A negative role identifier is real data, not an absent value.</b> <c>-1</c>, <c>-2</c> and
/// <c>-3</c> are persisted pseudo-principals that match no <c>dbo.Roles</c> row. Absence is expressed
/// by SQL <c>NULL</c> and by nothing else, so no value conversion between the two ever appears here.
/// The full argument, and the reason the schema declines to enforce a role foreign key because of it,
/// is recorded at the role relationship.
/// </para>
/// <para>
/// <b>Two of the four relationships cascade in the database and two enforce nothing.</b> The module
/// and catalogue references carry real <c>ON DELETE CASCADE</c> foreign keys and are mapped as such.
/// The account reference has a real foreign key with no <c>ON DELETE</c> clause, and the role
/// reference has no foreign key at all; both are therefore mapped with no delete behaviour, matching
/// what the database actually enforces rather than what a convention would assume.
/// </para>
/// <para>
/// <b>There are five indexes, and only the first is unique.</b> <c>IX_ModulePermission</c> spans the
/// module, the catalogue entry and both subject columns. Because this engine treats nulls as equal
/// for uniqueness, that admits exactly one subject-less grant per module and permission pair while
/// still distinguishing a role-targeted row from an account-targeted one. The remaining four are
/// single-column lookup indexes whose names follow the <i>referenced</i> table and are consequently
/// inconsistent about pluralisation; each is reproduced verbatim.
/// </para>
/// <para>
/// <b>Scope.</b> This type maps storage and nothing else. Permission <i>evaluation</i> - which of an
/// allow and a refusal wins, and what an absent grant means - lives in
/// <c>Infrastructure/Security/PermissionEvaluator.cs</c> and the authorisation policies that consume
/// it. No evaluation, query, service or dependency-registration logic appears here.
/// </para>
/// </remarks>
internal sealed class ModulePermissionConfiguration : IEntityTypeConfiguration<ModulePermission>
{
    /// <summary>
    /// Applies the mapping for the module grant entity type.
    /// </summary>
    /// <param name="builder">The builder for the module grant entity type.</param>
    public void Configure(EntityTypeBuilder<ModulePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: how the citations below were obtained, recorded so they can be re-checked rather
        //   than trusted. The 88 upgrade scripts under
        //   Website/Providers/DataProviders/SqlDataProvider/ are CRLF-terminated and template their
        //   object names as {databaseOwner}{objectQualifier}Name, so a naive search reports an object
        //   as absent when it is present. Every line reference here was taken after stripping the
        //   carriage returns, the two template tokens and the square brackets, searching
        //   case-insensitively, and excluding UnInstall.SqlDataProvider - 87 scripts.
        //
        //   Four hazards bite on this table specifically and each produced a false result before it
        //   was accounted for. First, the keyword CONSTRAINT frequently ends one line with the
        //   constraint name on the next - true of the primary key at 02.02.00:L716-L717 and of the
        //   unique index at 04.05.02:L134-L135 - so a single-line search finds neither. Second,
        //   legacy constraint naming is inconsistent: the grant-to-account key is
        //   FK_ModulePermissionUsers with NO separator at 04.05.00:L645, while its page-level twin is
        //   FK_TabPermission_Users with one at 04.05.00:L486, so a pattern anchored on an assumed
        //   separator reports this table's only real account key as missing. Third, the four lookup
        //   indexes at 04.06.00 sit immediately after TabPermission's four at L1181-L1204, so an
        //   off-by-one block read attributes one table's indexes to the other. Fourth, and least
        //   obvious, the normalising search strips the {databaseOwner} token but NOT a literal dbo.
        //   prefix, and the role keys in this schema are written REFERENCES dbo.Roles - so a sweep
        //   anchored on "REFERENCES Roles" returns zero hits and appears to prove an absence it has
        //   not actually tested. The absence proof at the role relationship below allows for the
        //   qualifier and flattens newlines for exactly that reason.
        //
        // MIGRATION: the legacy inheritance is deliberately gone, and it is the reason this file maps
        //   six columns rather than ten. Library/Components/Security/Permissions/ModulePermission.vb
        //   L28-L29 declares ModulePermissionInfo Inherits PermissionInfo, and its second constructor
        //   at L55-L63 copies ModuleDefID, PermissionCode, PermissionID, PermissionKey and
        //   PermissionName off the supplied base instance - so one legacy object surfaced both the
        //   assignment columns and a private, already-stale copy of the catalogue entry it referred
        //   to. The terminal table stores none of those catalogue columns. This configuration
        //   therefore splits on the physical table boundary: PermissionCode, PermissionKey,
        //   PermissionName and ModuleDefID are mapped only by PermissionConfiguration and are reached
        //   from here through the Permission navigation. The legacy carrier also exposed RoleName,
        //   Username and DisplayName (ModulePermission.vb L93, L120, L129), which are not columns of
        //   this table either - they are projections of the view discussed below.
        //
        // MIGRATION: Rule T8 - legacy machinery that deliberately produces nothing here. The
        //   pre-generics ModulePermissionCollection wrapper yields no target type, and with it goes
        //   the Overloads Overrides Equals at ModulePermission.vb L158-L165 whose own remarks
        //   (L149-L153) state it existed "to prevent adding duplicates to the
        //   ModulePermissionCollection". Uniqueness is the database's four-column index configured
        //   below, and identity equality comes from Entity<int>. The reflection-based row hydrator
        //   and the sentinel-translation helpers in Library/Components/Shared/Null.vb are likewise
        //   replaced by the provider's own materialiser.
        //
        // MIGRATION: Rule T4 - out-of-scope machinery that is deliberately NOT reproduced. The view
        //   vw_ModulePermissions, dropped and recreated at 04.05.00:L658-L688, is not mapped: no
        //   keyless type and no view mapping appears here. The legacy INSERT seed rows are not
        //   reproduced as model seed data - this configuration maps an existing table, it never
        //   writes to one. And no schema-altering or raw-SQL construct of any kind appears, because
        //   the baseline migration is intentionally empty and this mapping must be applicable to a
        //   live DotNetNuke database as a no-op.

        // MIGRATION: SINGULAR legacy table name. CREATE TABLE ModulePermission is declared once in
        //   the whole chain, at 02.02.00:L675, and the provider's pluralising convention would look
        //   for a ModulePermissions table that does not exist. Three of the four tables in this
        //   family are singular - Permission, ModulePermission and TabPermission - so the name is
        //   asserted per table rather than assumed folder-wide. Website/release.config:L354 registers
        //   objectQualifier as empty and :L355 registers databaseOwner as dbo, which is why the name
        //   is unprefixed and the schema is dbo.
        builder.ToTable("ModulePermission", "dbo");

        // MIGRATION: primary-key lineage. Added as PK_ModulePermission PRIMARY KEY CLUSTERED over
        //   ModulePermissionID at 02.02.00:L716-L720, dropped at 03.00.09:L461 and recreated
        //   clustered at 03.00.09:L477-L478 - the terminal form. The constraint is named explicitly
        //   so the model carries the name the database already has rather than one invented by
        //   convention. The key is the row's, not the subject's: which grants may coexist is the
        //   separate four-column unique index configured further down, and the two are independent.
        builder.HasKey(g => g.ModulePermissionId)
            .HasName("PK_ModulePermission");

        // MIGRATION: ModulePermissionID int IDENTITY (1, 1) NOT NULL at 02.02.00:L676. The column
        //   keeps the legacy upper-case ID spelling. The seed is recorded so a model-generated script
        //   would continue the existing sequence rather than restart it. A seed of 1 is the ordinary
        //   case and means no valid key on this table collides with the legacy absent-integer marker
        //   of -1 - but that local good fortune is emphatically not shared by the columns below:
        //   Modules.ModuleID and Roles.RoleID are both IDENTITY (0, 1) at 01.00.00:L221 and
        //   01.00.00:L115, so 0 is the first real module and the first real role, and nothing in this
        //   model may read 0 as "not saved yet".
        builder.Property(g => g.ModulePermissionId)
            .HasColumnName("ModulePermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: ModuleID int NOT NULL at 02.02.00:L677, never re-typed by any later script.
        //   Required, and backed by a real cascading foreign key configured below. Indexed at
        //   04.06.00:L1213-L1218.
        builder.Property(g => g.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: PermissionID int NOT NULL at 02.02.00:L678, never re-typed. Required, and backed
        //   by a real cascading foreign key configured below. This column is what REPLACES the legacy
        //   inheritance described at the top of this method: a grant names its catalogue entry and
        //   carries no copy of it. Indexed at 04.06.00:L1207-L1212.
        builder.Property(g => g.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: RoleID is NULLABLE in the terminal schema, and the provenance matters because
        //   the baseline declaration says otherwise. It was created int NOT NULL at 02.02.00:L679.
        //   04.05.00:L615-L637 then rebuilt it destructively through a temporary column: ADD
        //   TempRoleID int NULL (L615-L616), UPDATE SET TempRoleID = RoleID (L619-L620), DROP COLUMN
        //   RoleID (L623-L624), ADD RoleID int NULL (L627-L628), UPDATE SET RoleID = TempRoleID
        //   (L631-L632), DROP COLUMN TempRoleID (L635-L636). Reading only the CREATE TABLE yields the
        //   wrong nullability and a mapping that rejects rows the legacy application writes routinely,
        //   so this property is int? and is deliberately not marked required. TempRoleID is dropped
        //   inside that same block and exists in no terminal database; it is never mapped. Indexed
        //   at 04.06.00:L1225-L1230.
        //
        // MIGRATION: Rule T7 - this column carries the most dangerous sentinel collision in the
        //   folder, and no conversion may ever be installed on it. NEGATIVE values here are REAL,
        //   PERSISTED pseudo-principals, not markers for an absent value: -1 is All Users, -2 is
        //   Superuser and -3 is Unauthenticated Users, per the constants at
        //   Library/Components/Shared/Globals.vb L95-L97. The schema itself says so - the view at
        //   04.05.00:L669-L674 reads CASE MP.RoleID WHEN -1 THEN 'All Users' WHEN -2 THEN 'Superuser'
        //   WHEN -3 THEN 'Unauthenticated Users' ELSE R.RoleName END, and it reaches roles through a
        //   LEFT OUTER JOIN at L685 precisely because those three values match no Roles row. So a
        //   RoleID of -1 means "the All Users pseudo-role", NOT "absent". Absence is expressed ONLY
        //   by SQL NULL. Collapsing the two in either direction - null to -1, or -1 to null - would
        //   silently grant or revoke access to every visitor of every portal, which is why no value
        //   converter, no default value and no clamping appears on this property. A fourth constant,
        //   glbRoleNothing = -4 (Globals.vb L98), was the legacy in-memory "no role chosen" marker
        //   assigned by the constructor at ModulePermission.vb L47; null expresses that here and -4
        //   is not expected in a row. The value round-trips exactly as stored.
        builder.Property(g => g.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int");

        // MIGRATION: UserID did not exist in the original table. 04.05.00:L639-L642 adds it as int
        //   NULL, guarded by IF (SELECT COLUMNPROPERTY(OBJECT_ID('ModulePermission'), 'UserID',
        //   'AllowsNull')) IS Null so the script is re-runnable - which makes the column look
        //   conditional in the script while being unconditionally present in any 4.9 database. Its
        //   arrival is what makes a grant to a single account possible, so a terminal row targets
        //   either a role or an account. Nullable, therefore not marked required. Indexed at
        //   04.06.00:L1219-L1224.
        //
        // MIGRATION: Rule T7 again, in the opposite direction to RoleID. The legacy code
        //   distinguished a role grant from an account grant by testing this column against the -1
        //   sentinel - Null.IsNull(objModulePermission.UserID) at ModulePermissionController.vb L244 -
        //   so here -1 genuinely meant "no account" rather than "account number minus one". That test
        //   becomes a plain null check and -1 is never written to this column. The asymmetry with
        //   RoleID is deliberate and is the whole reason both notes exist: the same integer means
        //   "absent" on one column and "a real principal" on the other.
        builder.Property(g => g.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int");

        // MIGRATION: AllowAccess bit NOT NULL at 02.02.00:L680, declared WITHOUT a database default -
        //   unlike several bit columns on Modules, Tabs and TabModules, which is why no default value
        //   appears anywhere in this file. Required.
        //
        // MIGRATION: Rule T2 - this flag states what ONE row says and this configuration draws no
        //   conclusion from it. How a set of rows combines into an answer, and which of an allow and a
        //   refusal wins, is an evaluation rule belonging to
        //   Infrastructure/Security/PermissionEvaluator.cs. The distinction is load-bearing because
        //   the legacy rule is not the obvious one: ModulePermissionController.HasModulePermission
        //   matches on the permission key and the subject and never inspects this flag at all. A
        //   precedence rule restated here could contradict the component that actually applies it.
        builder.Property(g => g.AllowAccess)
            .HasColumnName("AllowAccess")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: this file declares FOUR relationships and it declares every one of them from the
        //   DEPENDENT end. The assembly scan that applies these configurations guarantees no
        //   ordering, so a relationship declared from both ends would let whichever end ran last
        //   silently decide the delete behaviour, with neither a compile error nor a model-validation
        //   error to reveal it. ModuleConfiguration, PermissionConfiguration, RoleConfiguration and
        //   UserConfiguration all deliberately decline to map their ModulePermissions collections for
        //   that reason, so no principal-side collection mapping and no HasMany appears here either.
        //   Requiredness is derived from property nullability - ModuleId and PermissionId are int, so
        //   those two are required; RoleId and UserId are int?, so those two are optional - and
        //   requiredness is never restated on a relationship builder.
        //
        // MIGRATION: NOT FOR REPLICATION and WITH NOCHECK, which several of these constraints carry
        //   in the scripts, have no counterpart in this API and are deliberately unrepresented
        //   throughout this folder.

        // MIGRATION: FK_ModulePermission_Modules FOREIGN KEY (ModuleID) REFERENCES Modules (ModuleID)
        //   ON DELETE CASCADE. Declared at 02.02.00:L761-L767, dropped at 03.00.09:L450-L451 and
        //   re-added at 03.00.09:L491-L493 - the terminal form. The cascade is real, so deleting a
        //   module withdraws its grants in the database whatever this model says, and the mapping
        //   states Cascade so the two agree. It is NOT downgraded to NoAction to avoid a multiple
        //   cascade path: the model validator does not reject multiple cascade paths, only the
        //   migration generator complains, and the baseline migration is intentionally empty per Rule
        //   T4. The constraint name is attached because the database already has it.
        builder.HasOne(g => g.Module)
            .WithMany(m => m.ModulePermissions)
            .HasForeignKey(g => g.ModuleId)
            .HasConstraintName("FK_ModulePermission_Modules")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: FK_ModulePermission_Permission FOREIGN KEY (PermissionID) REFERENCES Permission
        //   (PermissionID) ON DELETE CASCADE. Declared at 02.02.00:L768-L773, dropped at
        //   03.00.09:L447-L448 and re-added at 03.00.09:L491-L492 - the terminal form. Retiring a
        //   catalogue entry therefore withdraws the grants that named it. Reproduced faithfully as
        //   Cascade for the same reasons as the module key above.
        builder.HasOne(g => g.Permission)
            .WithMany(p => p.ModulePermissions)
            .HasForeignKey(g => g.PermissionId)
            .HasConstraintName("FK_ModulePermission_Permission")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: FOREIGN-KEY ABSENCE PROOF. There is NO physical foreign key from this table to
        //   Roles anywhere in the terminal schema, established two independent ways over all 87
        //   non-UnInstall scripts using the normalising search described at the top of this method.
        //   First, a census of every constraint name containing "Permission" returns nine foreign
        //   keys and no others - FK_ModulePermission_Modules, FK_ModulePermission_Permission,
        //   FK_ModulePermissionUsers, FK_TabPermission_Permission, FK_TabPermission_Tabs,
        //   FK_TabPermission_Users, FK_FolderPermission_Permission, FK_FolderPermission_Folders and
        //   FK_FolderPermissionUsers - not one of which references Roles under any spelling; a
        //   targeted sweep for a name containing both "Permission" and "Role" returns only the three
        //   IX_*Permission_Roles lookup INDEXES, never a key. Second, a sweep of every foreign key
        //   referencing the Roles table returns exactly one constraint, FK_UserRoles_Roles, declared
        //   at 01.00.00:L689-L694 and recreated at 01.00.04:L1389 and 01.00.05:L2812 - all three
        //   owned by UserRoles, none by a permission table.
        //
        // MIGRATION: the key is absent BY DESIGN, not by omission, and the reason is the sentinel
        //   note on RoleId above: the persisted pseudo-principals -1, -2 and -3 match no Roles row,
        //   so a real key would reject the very rows a live DotNetNuke database contains. Three
        //   consequences are deliberate. The relationship is declared here anyway - omitting it would
        //   let convention infer the key and apply its own default of ClientSetNull, which the schema
        //   does not do - so declaring it explicitly is what pins the behaviour. It is given
        //   NoAction, because the database enforces and cascades nothing here and a pseudo-principal
        //   has no principal row to cascade from. And NO constraint name is attached, precisely
        //   because there is no constraint to name. The practical caveat for callers is that this
        //   navigation may be null even when RoleId holds a value, so no query may rely on being able
        //   to include it; read RoleId for the fact.
        //
        // MIGRATION: do not generalise this finding in either direction. UserRole DOES have a real
        //   cascading foreign key to Roles - FK_UserRoles_Roles ... ON DELETE CASCADE at
        //   01.00.00:L689-L694 - while the three permission tables have none. Each was measured
        //   independently, and a sibling configuration copying either shape onto the other would
        //   contradict the schema.
        builder.HasOne(g => g.Role)
            .WithMany(r => r.ModulePermissions)
            .HasForeignKey(g => g.RoleId)
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: the account key EXISTS, and the only reason anyone would conclude otherwise is
        //   its name. It is FK_ModulePermissionUsers - with NO underscore before "Users" - declared at
        //   04.05.00:L644-L651, whereas its page-level twin is FK_TabPermission_Users with one at
        //   04.05.00:L486. A search anchored on an FK_<table>_<table> pattern therefore yields a
        //   FALSE ABSENCE here, and this note exists so nobody re-derives that wrong conclusion.
        //   FK_FolderPermissionUsers shares the quirk, which makes TabPermission the odd one out
        //   rather than this table.
        //
        // MIGRATION: the constraint is declared REFERENCES Users (UserID) NOT FOR REPLICATION with NO
        //   ON DELETE clause at 04.05.00:L648-L651, so the terminal behaviour is NoAction and the
        //   mapping says so rather than accepting a convention default. Unlike the role reference
        //   above this one is genuinely enforced, so a non-null UserId does name an existing account.
        //   The real constraint name is attached so the model carries what the database already has,
        //   missing separator included.
        builder.HasOne(g => g.User)
            .WithMany(u => u.ModulePermissions)
            .HasForeignKey(g => g.UserId)
            .HasConstraintName("FK_ModulePermissionUsers")
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: the terminal object set for this table is exactly ten objects, and every one is
        //   accounted for in this file. An exhaustive sweep of every CREATE TABLE and ALTER TABLE
        //   statement naming this table returns fifteen statements across five scripts, which resolve
        //   to: PK_ModulePermission, the unique index below, the four lookup indexes below,
        //   FK_ModulePermission_Modules, FK_ModulePermission_Permission, FK_ModulePermissionUsers,
        //   and the six columns. There is no foreign key to Roles, no column default, no check
        //   constraint and no surviving temporary column.
        //
        // MIGRATION: declared as a UNIQUE NONCLUSTERED table CONSTRAINT rather than as a standalone
        //   unique index - ALTER TABLE ModulePermission ADD CONSTRAINT IX_ModulePermission UNIQUE
        //   NONCLUSTERED (ModuleID, PermissionID, RoleID, UserID) ON PRIMARY at 04.05.02:L134-L141 -
        //   and the provider expresses both forms identically as a unique index. Never dropped. The
        //   column order is reproduced exactly. Both trailing columns are nullable and this engine
        //   treats nulls as equal for uniqueness, so a role-targeted row and an account-targeted row
        //   remain distinguished while exactly one subject-less grant is admitted per module and
        //   permission pair. The 04.05.02 script deletes duplicate tuples immediately before adding
        //   the constraint, using the explicit null-equality predicate at L123 - direct evidence that
        //   live installations held duplicates and that both subject columns are genuinely nullable,
        //   so this rule is load-bearing rather than decorative. The database is the enforcer; no
        //   check in this layer duplicates it.
        //
        // MIGRATION: THE FILTER IS EXPLICITLY SUPPRESSED, and on this table it is the difference
        //   between a constraint that works and one that does not. The SQL Server provider attaches a
        //   "RoleID IS NOT NULL AND UserID IS NOT NULL" predicate to a unique index over nullable
        //   columns unless told otherwise. Every real grant on this table targets EITHER a role OR an
        //   account, never both, so exactly one of those two columns is null on essentially every row -
        //   which means the generated filter would exclude essentially every row from uniqueness
        //   enforcement and permit the very duplicate tuples the 04.05.02 script was written to delete.
        //   The terminal object is a plain UNIQUE NONCLUSTERED table constraint with no predicate.
        //   Passing null as the filter removes the predicate and restores the declared rule.
        builder.HasIndex(g => new { g.ModuleId, g.PermissionId, g.RoleId, g.UserId })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ModulePermission");

        // MIGRATION: four single-column lookup indexes, one per foreign-key column plus one for the
        //   unenforced role column, added at 04.06.00:L1207-L1230 under IF NOT EXISTS (SELECT * FROM
        //   dbo.sysindexes WHERE name = N'...') guards. All four are NON-unique; only the four-column
        //   constraint above is unique.
        //
        //   Their names follow the REFERENCED table rather than any consistent rule, which is why the
        //   suffixes disagree about pluralisation: _Permission is singular because dbo.Permission is,
        //   while _Modules, _Users and _Roles are plural because those tables are. Each is carried
        //   verbatim and none is normalised - renaming one would make the model describe an index the
        //   database does not have.
        //
        //   These four sit immediately AFTER TabPermission's own four at 04.06.00:L1181-L1204, and
        //   the two blocks are near-identical in shape. They were read separately and confirmed
        //   distinct; conflating them would silently attribute one table's indexes to the other.
        builder.HasIndex(g => g.PermissionId)
            .HasDatabaseName("IX_ModulePermission_Permission");

        builder.HasIndex(g => g.ModuleId)
            .HasDatabaseName("IX_ModulePermission_Modules");

        builder.HasIndex(g => g.UserId)
            .HasDatabaseName("IX_ModulePermission_Users");

        builder.HasIndex(g => g.RoleId)
            .HasDatabaseName("IX_ModulePermission_Roles");
    }
}
