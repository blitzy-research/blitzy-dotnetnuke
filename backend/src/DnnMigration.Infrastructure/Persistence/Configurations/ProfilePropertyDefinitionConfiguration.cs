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
/// state, because the by-convention name is the pluralised one and no object in the database
/// answers to it.
/// </para>
/// <para>
/// Four properties are named differently from their columns and so are mapped explicitly:
/// <see cref="ProfilePropertyDefinition.ModuleDefinitionId"/> to <c>ModuleDefID</c>,
/// <see cref="ProfilePropertyDefinition.IsDeleted"/> to <c>Deleted</c>,
/// <see cref="ProfilePropertyDefinition.IsRequired"/> to <c>Required</c> and
/// <see cref="ProfilePropertyDefinition.IsVisible"/> to <c>Visible</c>. Two column types are the
/// widened terminal ones rather than the originals - <c>DefaultValue</c> is <c>ntext</c> and
/// <c>ValidationExpression</c> holds 2000 characters - and <c>PortalID</c> is nullable.
/// </para>
/// </remarks>
internal sealed class ProfilePropertyDefinitionConfiguration : IEntityTypeConfiguration<ProfilePropertyDefinition>
{
    /// <summary>Applies the mapping.</summary>
    /// <param name="builder">The builder for the profile property definition entity type.</param>
    public void Configure(EntityTypeBuilder<ProfilePropertyDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy table name is SINGULAR - CREATE TABLE ProfilePropertyDefinition at 04.00.04
        // - so the name is stated explicitly rather than derived. The by-convention name is the pluralised
        // entity name, which no object in this schema answers, and nothing would catch the difference at
        // build time: every query against the model would simply fail at run time against a table that does
        // not exist.
        //
        // MIGRATION: the create block is emitted twice and the pair is ONE logical change. It appears at
        // 03.02.03 and again, byte-identical and still wrapped in IF NOT EXISTS, at 04.00.04 - a
        // consolidated re-issue for installations that skipped the 3.x script, not a second table.
        builder.ToTable("ProfilePropertyDefinition", "dbo");

        // 04.00.04 (= 03.02.03) - ADD CONSTRAINT PK_ProfilePropertyDefinition PRIMARY KEY CLUSTERED
        // (PropertyDefinitionID), which no later script drops.
        builder.HasKey(d => d.PropertyDefinitionId).HasName("PK_ProfilePropertyDefinition");

        // 04.00.04 (= 03.02.03) - PropertyDefinitionID int IDENTITY(1,1) NOT NULL. This identity seeds at 1,
        // unlike Portals at -1 and Roles, Tabs and Modules at 0, so no valid key of this table collides with
        // the legacy "absent integer" marker of -1. That is a property of this table alone and is not
        // licence to read -1 or 0 as absence anywhere else.
        builder.Property(d => d.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: PortalID was created int NOT NULL (04.00.04 = 03.02.03), in which -1 denoted the
        // host-level definition, and was later widened by ALTER COLUMN PortalID int NULL (04.03.03 =
        // 03.03.03) under the heading "Change ProfilePropertyDefinition to use NULL instead of -1 for the
        // Host Portal". The very next statement migrated the data with UPDATE ProfilePropertyDefinition SET
        // PortalId = NULL WHERE PortalId = -1 (04.03.03 = 03.03.03).
        //
        // The schema itself already replaced the host-portal sentinel with a true SQL null, so the nullable
        // CLR type is the schema-faithful mapping and no null-to-sentinel translation may be reintroduced
        // here. Adding one would undo a deliberate upstream data change and hand every host-level definition
        // to the portal identified by -1, because Portals.PortalID is declared IDENTITY(-1, 1): a non-null
        // -1 is a genuine, addressable portal and 0 is the next one an installation creates.
        builder.Property(d => d.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // MIGRATION: four columns are spelled differently from their CLR properties - ModuleDefID for
        // ModuleDefinitionId, then Deleted, Required and Visible for the three properties that carry an Is
        // prefix for readability. The C# names are the readable ones; the schema's are not, and convention
        // would find none of the four.
        //
        // 04.00.04 (= 03.02.03) - ModuleDefID int NULL. Nullable since creation, so a portal-wide property
        // that belongs to no module definition is a null rather than a sentinel.
        builder.Property(d => d.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        // 04.00.04 (= 03.02.03) - Deleted bit NOT NULL, and deliberately with no database default: three of
        // this table's four bit columns declare none, so none is invented for them here. The legacy business
        // object declares no property for this column at all; it is carried on the entity as IsDeleted
        // because the column exists, is required, and is what the read paths filter on.
        builder.Property(d => d.IsDeleted)
            .HasColumnName("Deleted")
            .HasColumnType("bit")
            .IsRequired();

        // 04.00.04 (= 03.02.03) - DataType int NOT NULL. Mapped as a plain integer and deliberately not as
        // an enumeration: the legacy value is a row identifier from the Lists table, resolved at run time
        // rather than against a fixed set of members, so the admissible values are data rather than code.
        builder.Property(d => d.DataType)
            .HasColumnName("DataType")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: widened from nvarchar(50) (04.00.04 = 03.02.03) to ntext by ALTER COLUMN DefaultValue
        // ntext NULL (04.05.00) under the heading "Update DefaultValue in ProfilePropertyDefinition to
        // nText". The terminal type is pinned here and no length facet is set alongside it: ntext carries
        // none, so combining the two is a modelling error, and pinning the original 50 would truncate or
        // reject a default that a real installation already stores.
        builder.Property(d => d.DefaultValue)
            .HasColumnName("DefaultValue")
            .HasColumnType("ntext");

        // 04.00.04 (= 03.02.03) - PropertyCategory nvarchar(50) NOT NULL. Unicode, as every string column on
        // this table is.
        builder.Property(d => d.PropertyCategory)
            .HasColumnName("PropertyCategory")
            .HasMaxLength(50)
            .IsRequired();

        // 04.00.04 (= 03.02.03) - PropertyName nvarchar(50) NOT NULL. This is the trailing member of the
        // unique index below and the sole member of the lookup index, so its length bound is load-bearing
        // for both.
        builder.Property(d => d.PropertyName)
            .HasColumnName("PropertyName")
            .HasMaxLength(50)
            .IsRequired();

        // 04.00.04 (= 03.02.03) - Length int NOT NULL CONSTRAINT DF_ProfilePropertyDefinition_Length DEFAULT
        // 0. This is the ONLY column on the table that carries a database default, and the default is
        // described here so an insert that omits the value behaves as the database does rather than writing
        // a CLR zero that merely looks the same.
        builder.Property(d => d.Length)
            .HasColumnName("Length")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // 04.00.04 (= 03.02.03) - Required bit NOT NULL.
        //
        //  The property selector and the trailing call below are unrelated despite reading alike: the first
        //  names the CLR property IsRequired, the second states that the column is NOT NULL. This is
        //  intentional and must not be "simplified" away.
        builder.Property(d => d.IsRequired)
            .HasColumnName("Required")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: widened from nvarchar(100) (04.00.04 = 03.02.03) characters by ALTER COLUMN
        // ValidationExpression nvarchar(2000) (04.03.05). That statement omits an explicit NULL/NOT NULL and
        // the column was created nullable, so it remains nullable and is deliberately not marked required.
        builder.Property(d => d.ValidationExpression)
            .HasColumnName("ValidationExpression")
            .HasMaxLength(2000);

        // 04.00.04 (= 03.02.03) - ViewOrder int NOT NULL. A plain integer ordering hint with no database
        // default; the legacy editor metadata that also ordered these properties was Web Forms decoration
        // and does not survive.
        builder.Property(d => d.ViewOrder)
            .HasColumnName("ViewOrder")
            .HasColumnType("int")
            .IsRequired();

        // 04.00.04 (= 03.02.03) - Visible bit NOT NULL, again with no database default.
        builder.Property(d => d.IsVisible)
            .HasColumnName("Visible")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: the legacy index is an unfiltered CREATE UNIQUE INDEX over (PortalID ASC, ModuleDefID
        // ASC, PropertyName ASC) - 04.00.04 = 03.02.03 - and no script in the chain ever drops it. Both
        // leading columns are nullable and SQL Server treats nulls as equal for uniqueness, so the
        // host-level rows that carry a null PortalID after 04.03.03 are still constrained to one row per
        // name.
        //
        // The trailing filter argument is deliberately null. Left unset, this provider synthesises a
        // filtered index for a unique index over nullable columns, and such a filter excludes precisely the
        // host-level rows from the constraint - a strictly weaker rule than the database enforces today,
        // under which duplicate host-level property names could accumulate.
        builder.HasIndex(d => new { d.PortalId, d.ModuleDefinitionId, d.PropertyName })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ProfilePropertyDefinition");

        // 04.00.04 (= 03.02.03) - CREATE INDEX IX_ProfilePropertyDefinition_PropertyName ON
        // ProfilePropertyDefinition(PropertyName ASC). Deliberately NOT unique: the same property name
        // legitimately exists in many portals, so asserting a uniqueness the database does not have would
        // make the change tracker reject rows the database accepts.
        builder.HasIndex(d => d.PropertyName)
            .HasDatabaseName("IX_ProfilePropertyDefinition_PropertyName");

        // MIGRATION: 04.03.03 (= 03.03.03) adds CONSTRAINT FK_ProfilePropertyDefinition_Portals FOREIGN KEY
        // (PortalID) REFERENCES Portals (PortalID) ON DELETE CASCADE. The cascade is explicit in the
        // terminal schema and is reproduced exactly rather than softened: deleting a portal already removes
        // its profile property definitions in the legacy database, and weakening the rule here would let the
        // model attempt writes the database itself refuses.
        //
        // NOT FOR REPLICATION and WITH NOCHECK have no counterpart in the model and are left unrepresented
        // across this folder. The key reuses the column mapped above, so no shadow property appears.
        builder.HasOne(d => d.Portal)
            .WithMany()
            .HasForeignKey(d => d.PortalId)
            .HasConstraintName("FK_ProfilePropertyDefinition_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: there is NO physical foreign key from this table to ModuleDefinitions anywhere in the
        // terminal schema, and the model says so rather than assuming otherwise. A case-insensitive sweep
        // for FK_ProfilePropertyDefinition across every upgrade script in the chain - 87 files, the
        // uninstall script excluded, normalised for the templated databaseOwner and objectQualifier prefixes
        // and for the bracketed spelling, since a naive search returns nothing where the object exists -
        // yields exactly one constraint: FK_ProfilePropertyDefinition_Portals (04.03.03).
        //
        // ModuleDefID is therefore an unenforced reference. The relationship is mapped so that queries
        // traverse the existing column rather than a shadow one, and it is given NoAction and must never be
        // given a cascade: the database enforces nothing here, so cascading would delete rows the legacy
        // system keeps.
        builder.HasOne(d => d.ModuleDefinition)
            .WithMany(m => m.ProfilePropertyDefinitions)
            .HasForeignKey(d => d.ModuleDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
