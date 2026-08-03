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
/// The table name is singular in the schema. It arrived whole at 03.02.03, when the legacy profile
/// subsystem replaced a fixed set of nineteen columns on the user record with a definition table and
/// a value table, so there is no earlier shape to reconcile.
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
/// <c>ValidationExpression</c> is <c>nvarchar(2000)</c> - and <c>PortalID</c> is nullable. Each of
/// those five facts is a measured ALTER from the upgrade chain and is cited at the property it
/// governs.
/// </para>
/// <para>
/// The unique index <c>IX_ProfilePropertyDefinition</c> spans the tenant, the owning definition and
/// the property name together, so the same property name may exist once per tenant per owning
/// definition. A second, non-unique index exists over the name alone to support lookup by name
/// across tenants.
/// </para>
/// <para>
/// <c>ModuleDefID</c> is nullable and, unlike almost every other reference in this schema, has no
/// foreign key of its own — the upgrade chain never declares one. The relationship is still modelled
/// so that Entity Framework uses the real column instead of inventing a shadow one, but no delete
/// behaviour is attached, because the database enforces nothing here and inventing enforcement would
/// change behaviour rather than preserve it.
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

        builder.ToTable("ProfilePropertyDefinition", "dbo");

        builder.HasKey(d => d.PropertyDefinitionId).HasName("PK_ProfilePropertyDefinition");

        builder.Property(d => d.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: PortalID was created int NOT NULL (03.02.03 line 1065, re-issued at
        // 04.00.04 line 1110) and later widened by ALTER COLUMN PortalID int NULL
        // (03.03.03 lines 77-78, re-issued at 04.03.03 lines 77-78), immediately followed by
        // UPDATE ProfilePropertyDefinition SET PortalId = NULL WHERE PortalId = -1
        // (03.03.03 lines 81-83). The schema itself replaced the host-portal -1 sentinel with a
        // true SQL null, so the nullable CLR type is the schema-faithful mapping and no
        // null-to-sentinel conversion may be reintroduced here - doing so would undo a deliberate
        // upstream data migration. A non-null -1 or 0 remains a legitimate portal reference,
        // because Portals.PortalID is IDENTITY(-1, 1).
        builder.Property(d => d.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // MIGRATION: the four column names below differ from their CLR property names, so each
        // mapping is stated explicitly - convention would not find any of them.
        builder.Property(d => d.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        builder.Property(d => d.IsDeleted)
            .HasColumnName("Deleted")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(d => d.DataType)
            .HasColumnName("DataType")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: widened from nvarchar(50) (03.02.03 line 1069) to ntext by
        // ALTER COLUMN DefaultValue ntext NULL (04.05.00 lines 1593-1594). ntext carries no length
        // facet, so HasMaxLength is deliberately absent: pinning the original 50 would truncate or
        // reject a default that a real installation already stores.
        builder.Property(d => d.DefaultValue)
            .HasColumnName("DefaultValue")
            .HasColumnType("ntext");

        builder.Property(d => d.PropertyCategory)
            .HasColumnName("PropertyCategory")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(d => d.PropertyName)
            .HasColumnName("PropertyName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(d => d.Length)
            .HasColumnName("Length")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // The property selector and the trailing IsRequired() call are unrelated: the first names
        // the CLR property IsRequired, the second states that the column is NOT NULL.
        builder.Property(d => d.IsRequired)
            .HasColumnName("Required")
            .HasColumnType("bit")
            .IsRequired();

        // MIGRATION: widened from nvarchar(100) (03.02.03 line 1074) to nvarchar(2000) by
        // ALTER COLUMN ValidationExpression nvarchar(2000) (04.03.05 lines 16-17). That statement
        // omits an explicit NULL/NOT NULL, and the column was created nullable, so it stays
        // nullable and takes no IsRequired().
        builder.Property(d => d.ValidationExpression)
            .HasColumnName("ValidationExpression")
            .HasMaxLength(2000);

        builder.Property(d => d.ViewOrder)
            .HasColumnName("ViewOrder")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(d => d.IsVisible)
            .HasColumnName("Visible")
            .HasColumnType("bit")
            .IsRequired();

        builder.HasOne(d => d.Portal)
            .WithMany()
            .HasForeignKey(d => d.PortalId)
            .HasConstraintName("FK_ProfilePropertyDefinition_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.ModuleDefinition)
            .WithMany(m => m.ProfilePropertyDefinitions)
            .HasForeignKey(d => d.ModuleDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: the legacy index is an unfiltered CREATE UNIQUE INDEX over
        // (PortalID ASC, ModuleDefID ASC, PropertyName ASC) - 03.02.03 line 1082, re-issued at
        // 04.00.04 line 1127 - and SQL Server treats nulls as equal for uniqueness, so the
        // host-level rows that carry a null PortalID after 03.03.03 lines 81-83 are still
        // constrained to one row per name. HasFilter(null) suppresses the filtered index that this
        // provider would otherwise synthesise for a unique index over nullable columns: such a
        // filter excludes exactly those rows from the constraint and would let duplicate host-level
        // property names accumulate, which is a weaker rule than the database enforces today.
        builder.HasIndex(d => new { d.PortalId, d.ModuleDefinitionId, d.PropertyName })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ProfilePropertyDefinition");

        builder.HasIndex(d => d.PropertyName)
            .HasDatabaseName("IX_ProfilePropertyDefinition_PropertyName");
    }
}
