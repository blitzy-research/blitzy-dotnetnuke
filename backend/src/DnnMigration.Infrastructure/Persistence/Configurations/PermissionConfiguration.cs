using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="Permission"/> to the existing, immutable DotNetNuke 4.9 SQL Server table
/// <c>dbo.Permission</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table name is singular.</b> The terminal schema declares <c>Permission</c>, not
/// <c>Permission</c> pluralised, so the mapping states the name explicitly and the provider's
/// pluralising convention is deliberately overridden. Three of the four tables in this family are
/// singular, which is why the name is asserted per table here rather than assumed folder-wide.
/// </para>
/// <para>
/// <b>This is a catalogue, not an access-control list.</b> A row declares that a named action
/// <i>exists</i> for a given scope code and module definition. Whether a particular role or account
/// may perform it is a separate row in <c>dbo.ModulePermission</c> or <c>dbo.TabPermission</c> that
/// points back at this one, which is why this table carries no role, no account and no
/// allow-or-deny discriminator.
/// </para>
/// <para>
/// <b>Every text column on this table is ANSI, and that is unusual.</b> All three are declared
/// <c>varchar(50)</c> in the terminal schema, never the Unicode counterpart. The provider maps a
/// string property to the Unicode type by default, so each of the three says otherwise explicitly.
/// Letting the default stand would give every query parameter a different type from the column it
/// is compared against, which costs the unique index its usefulness for lookup and changes
/// comparison behaviour under a case-sensitive collation.
/// </para>
/// <para>
/// <b><see cref="PermissionKey"/> persists its member name, never its ordinal.</b> That
/// enumeration declares no explicit numeric values, so the member identifier is the stored value
/// and a string value conversion is the correct mapping. This is the deliberate opposite of the
/// billing-frequency columns on <c>dbo.Roles</c>, whose enumeration carries character literals and
/// therefore needs the dedicated converter that <c>RoleConfiguration</c> binds. Neither approach
/// may be cross-applied to the other table.
/// </para>
/// <para>
/// <b>The uniqueness rule spans three columns, and there is exactly one index.</b>
/// <c>IX_Permission</c> is unique over the scope code, the owning definition and the key together,
/// so the same key may exist once per scope code per definition and a second row naming the same
/// triple is rejected by the database rather than by any check in this layer.
/// </para>
/// <para>
/// <b>The owning-definition reference has no physical foreign key.</b> It is required and it is
/// mapped for querying, but the database enforces nothing, because a system-level row names no
/// particular definition. The proof and its consequences are recorded at the relationship itself.
/// </para>
/// <para>
/// <b>Scope.</b> This type maps storage and nothing else. Permission <i>evaluation</i> lives in
/// <c>Infrastructure/Security/PermissionEvaluator.cs</c> and the authorisation policies that
/// consume it; no evaluation, query, service or dependency-registration logic appears here.
/// </para>
/// </remarks>
internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    /// <summary>
    /// Applies the mapping for the permission catalogue entity type.
    /// </summary>
    /// <param name="builder">The builder for the permission catalogue entity type.</param>
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: legacy machinery that deliberately produces nothing here. The legacy carrier
        //   Library/Components/Security/Permissions/Permission.vb hid ModuleDefID (L60) and
        //   PermissionName (L78) behind an XML-serialisation suppression attribute and exposed the
        //   other three under lower-case element names, because that one type doubled as its own
        //   wire format. Rule T5: the wire contract now belongs to the Application-layer transfer
        //   objects and the storage contract belongs to this file, so no serialisation attribute and
        //   no attribute-based mapping of any kind is reproduced. Rule T8: the pre-generics
        //   ModulePermissionCollection and TabPermissionCollection wrappers, the reflection-based
        //   row hydrator and the sentinel-translation helpers in
        //   Library/Components/Shared/Null.vb all yield no target type; the provider's own
        //   materialiser replaces every one of them.
        //
        // MIGRATION: how the citations below were obtained, recorded so they can be re-checked
        //   rather than trusted. The 88 upgrade scripts under
        //   Website/Providers/DataProviders/SqlDataProvider/ are CRLF-terminated and template their
        //   object names as {databaseOwner}{objectQualifier}Name, so a naive search reports an
        //   object as absent when it is present. Every line reference here was taken after
        //   stripping the carriage returns, the two template tokens and the square brackets,
        //   searching case-insensitively, and excluding UnInstall.SqlDataProvider. Two further
        //   hazards bite on this table family specifically. The keyword CONSTRAINT frequently ends
        //   one line with the constraint name on the next - true of the primary key at
        //   02.02.00:L723-L724 and of the unique index at 04.05.02:L334-L335 - so a single-line
        //   search finds neither. And legacy constraint naming is inconsistent: the grant-to-account
        //   key is FK_ModulePermissionUsers with no separator at 04.05.00:L645 while its page-level
        //   twin is FK_TabPermission_Users with one at 04.05.00:L486, so an absence proof must
        //   search the bare table name with multi-line context and never a pattern anchored on an
        //   assumed separator.

        // MIGRATION: SINGULAR legacy table name. CREATE TABLE Permission is declared once in the
        //   whole chain, at 02.02.00:L684, and the provider's pluralising convention would look for
        //   a table that does not exist. Three of the four tables in this family are singular -
        //   Permission, ModulePermission and TabPermission - so this is asserted per table.
        //   Website/release.config:L354 registers objectQualifier as empty and :L355 registers
        //   databaseOwner as dbo, which is why the name is unprefixed and the schema is dbo.
        builder.ToTable("Permission", "dbo");

        // MIGRATION: primary-key lineage. Added as PK_Permission PRIMARY KEY CLUSTERED over
        //   PermissionID at 02.02.00:L723-L727, dropped at 03.00.09:L463 and recreated clustered at
        //   03.00.09:L475 - the terminal form. The constraint is named explicitly so the model
        //   carries the name the database already has rather than one invented by convention.
        builder.HasKey(p => p.PermissionId)
            .HasName("PK_Permission");

        // MIGRATION: PermissionID int IDENTITY (1, 1) NOT NULL at 02.02.00:L685. The column keeps
        //   the legacy upper-case ID spelling. The seed is recorded so a model-generated script
        //   would continue the existing sequence rather than restart it. Unlike Portals at -1 and
        //   Roles, Tabs and Modules at 0, a seed of 1 means no valid key on this table collides with
        //   the legacy absent-integer marker, so this key needs no sentinel care of its own - and
        //   that local good fortune is emphatically not a licence to read 0 as "not saved yet"
        //   anywhere, because the sibling tables do not share it.
        builder.Property(p => p.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: PermissionCode varchar (50) NOT NULL at 02.02.00:L686, never re-typed by any
        //   later script. ANSI, so the store type, the length and the non-Unicode facet are all
        //   stated: the provider's default for a string property is the Unicode type, and accepting
        //   that default would silently change the storage type of this column. Deliberately a
        //   plain string rather than an enumeration - nothing constrains it, so an installation
        //   carrying a scope code this migration has never seen must round-trip it intact. The
        //   shipped vocabulary is SYSTEM_MODULE_DEFINITION, SYSTEM_TAB and SYSTEM_FOLDER.
        builder.Property(p => p.PermissionCode)
            .HasColumnName("PermissionCode")
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        // MIGRATION: the property is spelled ModuleDefinitionId in full, but the column keeps the
        //   abbreviated legacy spelling ModuleDefID, declared int NOT NULL at 02.02.00:L687. That
        //   abbreviation is the spelling every table referencing a definition uses, so the explicit
        //   column name below is the single thing standing between this model and a query against a
        //   column that does not exist. The column is NOT NULL here, which is per-table and not a
        //   family rule: the same column is nullable on ModuleControls and on
        //   ProfilePropertyDefinition.
        //
        // MIGRATION: the value -1 in this column marks a SYSTEM-level permission and is REAL DATA -
        //   it is not the legacy absent-integer sentinel that Library/Components/Shared/Null.vb
        //   defined as -1. Rule T7 keeps that sentinel from entering the domain, and this is the one
        //   place on this table where the distinction bites: because the column is NOT NULL, -1 cannot
        //   possibly mean "absent", so it can only mean "system level". It is therefore never
        //   translated to a null, never mapped to a nullable property and never read as a missing
        //   value. System-level rows are exactly the rows the legacy scripts key by scope code
        //   instead - SYSTEM_TAB at 02.02.00:L1265, SYSTEM_MODULE_DEFINITION at :L1072 and
        //   SYSTEM_FOLDER at :L1713 - and they belong to no particular definition.
        builder.Property(p => p.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: the conversion contract for this column, which is the single most consequential
        //   decision in this file. The DnnMigration.Domain.Enums.PermissionKey enumeration declares
        //   VIEW, EDIT, READ and WRITE with NO explicit numeric values, so the MEMBER NAME is the
        //   persisted value and the generic string conversion below is exactly right: it writes the
        //   member identifier and reads the same spelling back, resolving a stored value by name and
        //   refusing anything it does not recognise rather than substituting a member. That refusal
        //   is the desired behaviour and needs no defensive fallback - silently degrading an unknown
        //   key to the first member would turn it into a VIEW grant, the one failure mode a security
        //   vocabulary must not have. Converting to a number instead would write 0, 1, 2 or 3 into a
        //   text column and every legacy row and every legacy predicate would stop matching. This is
        //   also the deliberate opposite of the billing-frequency columns on dbo.Roles, whose
        //   enumeration carries character literals and therefore needs the dedicated
        //   BillingFrequencyToStringConverter that RoleConfiguration binds; neither table's approach
        //   may be cross-applied to the other. The member names are load-bearing data a production
        //   database already holds, so no member may be renamed, re-cased or added, no bit-mask
        //   attribute may be introduced, and the sibling PermissionCode column stays a plain string.
        //   The property is non-nullable, so the conversion needs no null handling.
        //
        // MIGRATION: the width is the TERMINAL width, 50 and not 20. The column is created
        //   PermissionKey varchar (20) NOT NULL at 02.02.00:L688 and widened by 04.06.00:L397-L398
        //   with ALTER COLUMN PermissionKey varchar(50) not null, under the script's own comment
        //   "enlarge permission key field" at 04.06.00:L394. That statement re-states the column as
        //   required, so it remains NOT NULL. Nothing later in the chain touches it. Rule T4 makes
        //   the terminal state the schema, so a mapping that copied the baseline width would reject
        //   values the live column accepts.
        builder.Property(p => p.PermissionKey)
            .HasColumnName("PermissionKey")
            .HasConversion<string>()
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        // MIGRATION: PermissionName varchar (50) NOT NULL at 02.02.00:L689, never re-typed. ANSI for
        //   the same reason as PermissionCode above. Display text only - the legacy permission grids
        //   rendered it while the legacy carrier hid it from its own wire format - and it is never
        //   matched on, because PermissionKey is the machine-readable identity of the action.
        builder.Property(p => p.PermissionName)
            .HasColumnName("PermissionName")
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        // MIGRATION: the terminal object set for this table is exactly two objects, and this index is
        //   the second of them. An exhaustive sweep of every ALTER TABLE statement naming this table
        //   returns five and only five: the primary key added at 02.02.00:L723, dropped at
        //   03.00.09:L463 and recreated at 03.00.09:L475, this index added at 04.05.02:L334, and the
        //   key-column widening at 04.06.00:L397. So the table has no foreign key, no column
        //   default, no check constraint and no rebuild, which is why no default value and no
        //   constraint name appears anywhere in this file.
        //
        // MIGRATION: declared as a UNIQUE NONCLUSTERED table CONSTRAINT rather than as a standalone
        //   unique index - ALTER TABLE Permission ADD CONSTRAINT IX_Permission UNIQUE NONCLUSTERED
        //   (PermissionCode, ModuleDefID, PermissionKey) ON PRIMARY at 04.05.02:L334-L341 - and the
        //   provider expresses both forms identically as a unique index. Never dropped. The column
        //   order is reproduced exactly. The declaration follows the key property above so the string
        //   conversion is already registered and the index is built over the converted column, which
        //   is correct and needs no special handling. The 04.05.02 script removes duplicate triples
        //   before adding the constraint, direct evidence that live installations held them, so this
        //   rule is load-bearing rather than decorative.
        //
        // MIGRATION: Rule T4 also forbids seeding. This table is reference data that the legacy
        //   chain populates through its AddPermission procedure at 02.02.00:L843-L862, recreated at
        //   04.06.00:L404, and none of those rows is reproduced as model seed data here. The
        //   configuration maps an existing table; it never writes to one.
        builder.HasIndex(p => new { p.PermissionCode, p.ModuleDefinitionId, p.PermissionKey })
            .IsUnique()
            .HasDatabaseName("IX_Permission");

        // MIGRATION: FOREIGN-KEY ABSENCE PROOF. There is NO physical foreign key from this table to
        //   ModuleDefinitions anywhere in the terminal schema, established two independent ways over
        //   all 87 non-UnInstall scripts using the normalising search described at the top of this
        //   method. First, a census of every constraint name containing "Permission" returns exactly
        //   seventeen names - PK_Permission, PK_ModulePermission, PK_TabPermission,
        //   PK_FolderPermission, IX_Permission, IX_ModulePermission, IX_TabPermission,
        //   IX_FolderPermission, FK_ModulePermission_Permission, FK_ModulePermission_Modules,
        //   FK_ModulePermissionUsers, FK_TabPermission_Permission, FK_TabPermission_Tabs,
        //   FK_TabPermission_Users, FK_FolderPermission_Permission, FK_FolderPermission_Folders and
        //   FK_FolderPermissionUsers - and not one of them is an FK_Permission_ key of any spelling.
        //   Second, a sweep of every REFERENCES ModuleDefinitions occurrence returns exactly two,
        //   both owned by other tables: FK_PortalModuleDefinitions_ModuleDefinitions at
        //   01.00.05:L474-L477 and FK_ModuleControls_ModuleDefinitions at 02.00.00:L5026-L5029.
        //   Every constraint that names this table points the other way, INTO it.
        //
        // MIGRATION: the reference is unenforced BY DESIGN, not by omission. A system-level row
        //   carries ModuleDefID = -1, which matches no ModuleDefinitions row, so a real key would
        //   reject the very rows the legacy installer creates. Two consequences follow and both are
        //   deliberate. The relationship is declared here so the mapping uses the real ModuleDefID
        //   column rather than inventing a shadow one, and it is given no delete behaviour: the
        //   database cascades nothing from this side, and a system-level reference has no principal
        //   row to cascade from. Because the column is NOT NULL the relationship is required, which
        //   is unavoidable and accepted - the practical caveat for callers is that the navigation is
        //   NOT loadable for a system-level row, so no query may rely on being able to include it.
        //   No constraint name is attached, precisely because there is no constraint to name.
        //
        // MIGRATION: this is the only relationship this file declares, and it declares it from the
        //   dependent end. The assembly scan that applies these configurations guarantees no
        //   ordering, so a relationship declared from both ends would let whichever end ran last
        //   silently decide the delete behaviour, with neither a compile error nor a
        //   model-validation error to reveal it. This table is also the principal of two grant
        //   tables whose keys DO cascade in the database - FK_ModulePermission_Permission at
        //   03.00.09:L492 and FK_TabPermission_Permission at 03.00.09:L488 - and both are owned by
        //   ModulePermissionConfiguration and TabPermissionConfiguration respectively. Declaring
        //   either inverse here would race with those files and could soften a cascade the scripts
        //   record, so this file declares no principal-side collection mapping at all.
        builder.HasOne(p => p.ModuleDefinition)
            .WithMany(d => d.Permissions)
            .HasForeignKey(p => p.ModuleDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
