using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="ProfilePropertyDefinition"/> entity to the legacy
/// <c>dbo.ProfilePropertyDefinition</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table name is singular in this schema, which is the first thing this configuration has to
/// state, because the by-convention name is the pluralised one and no object in the database answers
/// to it. The table arrived whole at 03.02.03, when the legacy profile subsystem replaced a fixed set
/// of columns on the user record with a definition table and a value table, so there is no earlier
/// shape to reconcile - only four measured column changes afterwards.
/// </para>
/// <para>
/// Deletion is logical here: the <c>Deleted</c> flag - mapped from
/// <see cref="ProfilePropertyDefinition.IsDeleted"/> - marks a definition as withdrawn while its
/// stored values survive, which is why the repository exposes an explicit switch for including
/// withdrawn definitions rather than filtering them unconditionally.
/// </para>
/// <para>
/// Four properties are named differently from their columns and so are mapped explicitly:
/// <see cref="ProfilePropertyDefinition.ModuleDefinitionId"/> to <c>ModuleDefID</c>,
/// <see cref="ProfilePropertyDefinition.IsDeleted"/> to <c>Deleted</c>,
/// <see cref="ProfilePropertyDefinition.IsRequired"/> to <c>Required</c> and
/// <see cref="ProfilePropertyDefinition.IsVisible"/> to <c>Visible</c>. Two column types are the
/// widened terminal ones rather than the originals - <c>DefaultValue</c> is <c>ntext</c> and
/// <c>ValidationExpression</c> holds 2000 characters - and <c>PortalID</c> is nullable. Each of those
/// five facts is a measured change from the upgrade chain and is cited at the property it governs.
/// </para>
/// <para>
/// The unique index <c>IX_ProfilePropertyDefinition</c> spans the tenant, the owning definition and
/// the property name together, so the same property name may exist once per tenant per owning
/// definition. A second, non-unique index exists over the name alone to support lookup by name
/// across tenants.
/// </para>
/// <para>
/// <c>ModuleDefID</c> is nullable and, unlike almost every other reference in this schema, has no
/// foreign key of its own - the upgrade chain never declares one. The relationship is still modelled
/// so that Entity Framework traverses the real column instead of inventing a shadow one, but no
/// cascade is attached, because the database enforces nothing here and inventing enforcement would
/// change behaviour rather than preserve it.
/// </para>
/// <para>
/// <b>Two relationships are declared here, and exactly two.</b> This entity carries both key
/// columns, so it is the dependent half of both relationships and therefore owns both. The
/// assembly scan that applies these configurations guarantees no ordering, so a relationship
/// restated from its principal side is neither a compile error nor a model-validation error - it
/// simply lets whichever end ran last decide the delete behaviour, which could silently soften the
/// cascade the scripts record. The inverse relationship to the profile value rows is likewise owned
/// by the value configuration, not restated here.
/// </para>
/// </remarks>
internal sealed class ProfilePropertyDefinitionConfiguration : IEntityTypeConfiguration<ProfilePropertyDefinition>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the profile property definition entity type.</param>
    public void Configure(EntityTypeBuilder<ProfilePropertyDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy table name is SINGULAR - CREATE TABLE ProfilePropertyDefinition at
        //   04.00.04:L1107 - so the name is stated explicitly rather than derived. The
        //   by-convention name is the pluralised entity name, which no object in this schema
        //   answers to, and nothing would catch the difference at build time: every query against
        //   the model would simply fail at run time against a table that does not exist. This one
        //   call is the only thing standing between the model and that outcome.
        //
        // MIGRATION: the create block is emitted twice and the pair is ONE logical change. It
        //   appears at 03.02.03:L1062 and again, byte-identical and still wrapped in
        //   IF NOT EXISTS, at 04.00.04:L1107 - a consolidated re-issue for installations that
        //   skipped the 3.x script, not a second table. The 4.x line number is the 3.x number plus
        //   45 in that block, and both are cited below so the mapping is traceable from either
        //   upgrade line. The same duplication applies to the nullable-PortalID change and to the
        //   foreign key, cited at the members they govern.
        //
        // release.config:L354 sets objectQualifier to the empty string and :L355 sets databaseOwner
        // to dbo, so the physical name carries no prefix and resolves in the dbo schema.
        builder.ToTable("ProfilePropertyDefinition", "dbo");

        // 04.00.04:L1124-1125 (= 03.02.03:L1079-1080) - ADD CONSTRAINT
        // PK_ProfilePropertyDefinition PRIMARY KEY CLUSTERED (PropertyDefinitionID), which no later
        // script drops. The constraint name is stated so the model carries the name the database
        // already has rather than one derived by convention.
        builder.HasKey(d => d.PropertyDefinitionId).HasName("PK_ProfilePropertyDefinition");

        // 04.00.04:L1109 (= 03.02.03:L1064) - PropertyDefinitionID int IDENTITY(1,1) NOT NULL. The
        // seed is recorded so a generated script would continue the existing sequence rather than
        // restart it. Note the upper-case ID in the column name.
        //
        // This identity seeds at 1, unlike Portals at -1 and Roles, Tabs and Modules at 0, so no
        // valid key of this table collides with the legacy "absent integer" marker of -1. That is a
        // property of this table alone and is not licence to read -1 or 0 as absence anywhere else.
        builder.Property(d => d.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: PortalID was created int NOT NULL (04.00.04:L1110 = 03.02.03:L1065), in which
        //   -1 denoted the host-level definition, and was later widened by
        //   ALTER COLUMN PortalID int NULL (04.03.03:L77-78 = 03.03.03:L77-78) under the heading
        //   "Change ProfilePropertyDefinition to use NULL instead of -1 for the Host Portal". The
        //   very next statement migrated the data with
        //   UPDATE ProfilePropertyDefinition SET PortalId = NULL WHERE PortalId = -1
        //   (04.03.03:L81-83 = 03.03.03:L81-83).
        //
        //   The schema itself already replaced the host-portal sentinel with a true SQL null, so the
        //   nullable CLR type is the schema-faithful mapping and no null-to-sentinel translation may
        //   be reintroduced here. Adding one would undo a deliberate upstream data change and hand
        //   every host-level definition to the portal identified by -1, because Portals.PortalID is
        //   declared IDENTITY(-1, 1): a non-null -1 is a genuine, addressable portal and 0 is the
        //   next one an installation creates. Absence is expressed in this column only by null.
        //
        //   This table is the clearest case in this folder of the schema having performed that
        //   conversion already, which is exactly why this mapping adds nothing further.
        builder.Property(d => d.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // MIGRATION: four columns are spelled differently from their CLR properties - ModuleDefID
        //   for ModuleDefinitionId, then Deleted, Required and Visible for the three properties that
        //   carry an Is prefix for readability. The C# names are the readable ones; the schema's are
        //   not, and convention would find none of the four. Each mapping below is therefore stated
        //   explicitly, and one wrong character binds the model to a column that does not exist.
        //
        // 04.00.04:L1111 (= 03.02.03:L1066) - ModuleDefID int NULL. Nullable since creation, so a
        // portal-wide property that belongs to no module definition is a null rather than a
        // sentinel. The abbreviated spelling is not local to this table: Modules, ModuleControls and
        // Permission all reference a definition by the same abbreviated column name.
        builder.Property(d => d.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        // 04.00.04:L1112 (= 03.02.03:L1067) - Deleted bit NOT NULL, and deliberately with no
        // database default: three of this table's four bit columns declare none, so none is invented
        // for them here. The legacy business object declares no property for this column at all; it
        // is carried on the entity as IsDeleted because the column exists, is required, and is what
        // the read paths filter on.
        builder.Property(d => d.IsDeleted)
            .HasColumnName("Deleted")
            .HasColumnType("bit")
            .IsRequired();

        // 04.00.04:L1113 (= 03.02.03:L1068) - DataType int NOT NULL. Mapped as a plain integer and
        // deliberately not as an enumeration: the legacy value is a row identifier from the Lists
        // table, resolved at run time rather than against a fixed set of members, so the admissible
        // values are data rather than code. No enumeration of this folder's nine is imported for it.
        builder.Property(d => d.DataType)
            .HasColumnName("DataType")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: widened from nvarchar(50) (04.00.04:L1114 = 03.02.03:L1069) to ntext by
        //   ALTER COLUMN DefaultValue ntext NULL (04.05.00:L1593-1594) under the heading "Update
        //   DefaultValue in ProfilePropertyDefinition to nText". The terminal type is pinned here
        //   and no length facet is set alongside it: ntext carries none, so combining the two is a
        //   modelling error, and pinning the original 50 would truncate or reject a default that a
        //   real installation already stores. Nullable since creation.
        builder.Property(d => d.DefaultValue)
            .HasColumnName("DefaultValue")
            .HasColumnType("ntext");

        // 04.00.04:L1115 (= 03.02.03:L1070) - PropertyCategory nvarchar(50) NOT NULL. Unicode, as
        // every string column on this table is.
        builder.Property(d => d.PropertyCategory)
            .HasColumnName("PropertyCategory")
            .HasMaxLength(50)
            .IsRequired();

        // 04.00.04:L1116 (= 03.02.03:L1071) - PropertyName nvarchar(50) NOT NULL. This is the
        // trailing member of the unique index below and the sole member of the lookup index, so its
        // length bound is load-bearing for both.
        builder.Property(d => d.PropertyName)
            .HasColumnName("PropertyName")
            .HasMaxLength(50)
            .IsRequired();

        // 04.00.04:L1117 (= 03.02.03:L1072) - Length int NOT NULL CONSTRAINT
        // DF_ProfilePropertyDefinition_Length DEFAULT 0. This is the ONLY column on the table that
        // carries a database default, and the default is described here so an insert that omits the
        // value behaves as the database does rather than writing a CLR zero that merely looks the
        // same.
        builder.Property(d => d.Length)
            .HasColumnName("Length")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // 04.00.04:L1118 (= 03.02.03:L1073) - Required bit NOT NULL.
        //
        // The property selector and the trailing call below are unrelated despite reading alike: the
        // first names the CLR property IsRequired, the second states that the column is NOT NULL.
        // This is intentional and must not be "simplified" away.
        builder.Property(d => d.IsRequired)
            .HasColumnName("Required")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: widened from nvarchar(100) (04.00.04:L1119 = 03.02.03:L1074) to 2000
        //   characters by ALTER COLUMN ValidationExpression nvarchar(2000) (04.03.05:L16-17). That
        //   statement omits an explicit NULL/NOT NULL and the column was created nullable, so it
        //   remains nullable and is deliberately not marked required. The stored value is a regular
        //   expression, and the original bound is far too small for the ones a real installation
        //   holds - which is why the chain widened it.
        builder.Property(d => d.ValidationExpression)
            .HasColumnName("ValidationExpression")
            .HasMaxLength(2000);

        // 04.00.04:L1120 (= 03.02.03:L1075) - ViewOrder int NOT NULL. A plain integer ordering hint
        // with no database default; the legacy editor metadata that also ordered these properties
        // was Web Forms decoration and does not survive.
        builder.Property(d => d.ViewOrder)
            .HasColumnName("ViewOrder")
            .HasColumnType("int")
            .IsRequired();

        // 04.00.04:L1121 (= 03.02.03:L1076) - Visible bit NOT NULL, again with no database default.
        builder.Property(d => d.IsVisible)
            .HasColumnName("Visible")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: the legacy index is an unfiltered CREATE UNIQUE INDEX over
        //   (PortalID ASC, ModuleDefID ASC, PropertyName ASC) - 04.00.04:L1127 =
        //   03.02.03:L1082 - and no script in the chain ever drops it. Both leading columns are
        //   nullable and SQL Server treats nulls as equal for uniqueness, so the host-level rows
        //   that carry a null PortalID after 04.03.03:L81-83 are still constrained to one row per
        //   name. The column order is reproduced exactly, because it is what makes a property name
        //   unique per portal per contributing module definition rather than globally.
        //
        //   The trailing filter argument is deliberately null. Left unset, this provider synthesises
        //   a filtered index for a unique index over nullable columns, and such a filter excludes
        //   precisely the host-level rows from the constraint - a strictly weaker rule than the
        //   database enforces today, under which duplicate host-level property names could
        //   accumulate.
        builder.HasIndex(d => new { d.PortalId, d.ModuleDefinitionId, d.PropertyName })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ProfilePropertyDefinition");

        // 04.00.04:L1128 (= 03.02.03:L1083) - CREATE INDEX
        // IX_ProfilePropertyDefinition_PropertyName ON ProfilePropertyDefinition(PropertyName ASC).
        // Deliberately NOT unique: the same property name legitimately exists in many portals, so
        // asserting a uniqueness the database does not have would make the change tracker reject
        // rows the database accepts. The physical name is preserved.
        builder.HasIndex(d => d.PropertyName)
            .HasDatabaseName("IX_ProfilePropertyDefinition_PropertyName");

        // MIGRATION: 04.03.03:L86-88 (= 03.03.03:L86-88) adds CONSTRAINT
        //   FK_ProfilePropertyDefinition_Portals FOREIGN KEY (PortalID) REFERENCES
        //   Portals (PortalID) ON DELETE CASCADE. The cascade is explicit in the terminal schema and
        //   is reproduced exactly rather than softened: deleting a portal already removes its
        //   profile property definitions in the legacy database, and weakening the rule here would
        //   let the model attempt writes the database itself refuses. Several tables cascade from
        //   Portals, which is safe because the model validator does not reject multiple cascade
        //   paths - only a script generator objects, and the baseline schema for this migration
        //   carries a deliberately empty body, so no such script is produced (Rule T4).
        //
        //   NOT FOR REPLICATION and WITH NOCHECK have no counterpart in the model and are left
        //   unrepresented across this folder. The key reuses the column mapped above, so no shadow
        //   property appears.
        //
        // MIGRATION: the inverse side is left unnamed rather than invented. Portal declares no
        //   collection navigation back to this entity - it exposes aliases, modules, tabs, roles,
        //   per-portal memberships and available module types, and nothing for profile property
        //   definitions - so this mapping names no principal-side member and adds none. The
        //   relationship is optional because the key property is nullable, which the model derives
        //   for itself and which must not be overridden.
        builder.HasOne(d => d.Portal)
            .WithMany()
            .HasForeignKey(d => d.PortalId)
            .HasConstraintName("FK_ProfilePropertyDefinition_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: there is NO physical foreign key from this table to ModuleDefinitions anywhere
        //   in the terminal schema, and the model says so rather than assuming otherwise. A
        //   case-insensitive sweep for FK_ProfilePropertyDefinition across every upgrade script in
        //   the chain - 87 files, the uninstall script excluded, normalised for the templated
        //   databaseOwner and objectQualifier prefixes and for the bracketed spelling, since a naive
        //   search returns nothing where the object exists - yields exactly one constraint:
        //   FK_ProfilePropertyDefinition_Portals (04.03.03:L86-88). The complete set of constraints
        //   this table carries is PK_ProfilePropertyDefinition,
        //   DF_ProfilePropertyDefinition_Length, IX_ProfilePropertyDefinition,
        //   IX_ProfilePropertyDefinition_PropertyName and that single key. Nothing was dropped
        //   either: the sweep for drops against this table, its indexes and its key is empty.
        //
        //   ModuleDefID is therefore an unenforced reference. The relationship is mapped so that
        //   queries traverse the existing column rather than a shadow one, and it is given NoAction
        //   and must never be given a cascade: the database enforces nothing here, so cascading
        //   would delete rows the legacy system keeps. Nor may the relationship simply be omitted -
        //   left unmapped, convention would discover the key by name and apply its own default
        //   delete behaviour, which is the very thing being pinned down. The same finding holds for
        //   the definition reference on Permission, whose configuration maps it the same way.
        builder.HasOne(d => d.ModuleDefinition)
            .WithMany(m => m.ProfilePropertyDefinitions)
            .HasForeignKey(d => d.ModuleDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
