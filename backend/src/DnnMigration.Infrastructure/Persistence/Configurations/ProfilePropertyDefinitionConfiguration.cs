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
/// The table name is singular in this schema, which is the first thing this configuration has to state,
/// because the by-convention name is the pluralised one and no object in the database answers to it.
/// </para>
/// <para>
/// Four properties are named differently from their columns and so are mapped explicitly: <see
/// cref="ProfilePropertyDefinition.ModuleDefinitionId"/> to <c>ModuleDefID</c>, <see
/// cref="ProfilePropertyDefinition.IsDeleted"/> to <c>Deleted</c>, <see
/// cref="ProfilePropertyDefinition.IsRequired"/> to <c>Required</c> and <see
/// cref="ProfilePropertyDefinition.IsVisible"/> to <c>Visible</c>.
/// </para>
/// </remarks>
internal sealed class ProfilePropertyDefinitionConfiguration : IEntityTypeConfiguration<ProfilePropertyDefinition>
{
    /// <summary>Applies the mapping.</summary>
    /// <param name="builder">The builder for the profile property definition entity type.</param>
    public void Configure(EntityTypeBuilder<ProfilePropertyDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The legacy table name is SINGULAR - CREATE TABLE ProfilePropertyDefinition at 04.00.04 - so the
        // name is stated explicitly rather than derived.
        builder.ToTable("ProfilePropertyDefinition", "dbo");

        // 04.00.04 (= 03.02.03) - ADD CONSTRAINT PK_ProfilePropertyDefinition PRIMARY KEY CLUSTERED
        // (PropertyDefinitionID), which no later script drops.
        builder.HasKey(d => d.PropertyDefinitionId).HasName("PK_ProfilePropertyDefinition");

        // 04.00.04 (= 03.02.03) - PropertyDefinitionID int IDENTITY(1,1) NOT NULL. This identity seeds at
        // 1, unlike Portals at -1 and Roles, Tabs and Modules at 0, so no valid key of this table collides
        // with the legacy "absent integer" marker of -1.
        builder.Property(d => d.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // PortalID was created int NOT NULL (04.00.04 = 03.02.03), in which -1 denoted the host-level
        // definition, and was later widened by ALTER COLUMN PortalID int NULL (04.03.03 03.03.03) under the
        // heading "Change ProfilePropertyDefinition to use NULL instead of -1 for the Host Portal".
        builder.Property(d => d.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // Four columns are spelled differently from their CLR properties - ModuleDefID for
        // ModuleDefinitionId, then Deleted, Required and Visible for the three properties that carry an Is
        // prefix for readability.
        builder.Property(d => d.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        // 04.00.04 (= 03.02.03) - Deleted bit NOT NULL, and deliberately with no database default: three of
        // this table's four bit columns declare none, so none is invented for them here.
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

        // Widened from nvarchar(50) (04.00.04 = 03.02.03) to ntext by ALTER COLUMN DefaultValue ntext NULL
        // (04.05.00) under the heading "Update DefaultValue in ProfilePropertyDefinition to nText".
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

        // 04.00.04 (= 03.02.03) - Length int NOT NULL CONSTRAINT DF_ProfilePropertyDefinition_Length
        // DEFAULT 0.
        builder.Property(d => d.Length)
            .HasColumnName("Length")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // 04.00.04 (= 03.02.03) - Required bit NOT NULL.
        builder.Property(d => d.IsRequired)
            .HasColumnName("Required")
            .HasColumnType("bit")
            .IsRequired();

        // Widened from nvarchar(100) (04.00.04 = 03.02.03) characters by ALTER COLUMN ValidationExpression
        // nvarchar(2000) (04.03.05). That statement omits an explicit NULL/NOT NULL and the column was
        // created nullable, so it remains nullable and is deliberately not marked required.
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

        // The legacy index is an unfiltered CREATE UNIQUE INDEX over (PortalID ASC, ModuleDefID ASC,
        // PropertyName ASC) - 04.00.04 = 03.02.03 - and no script in the chain ever drops it.
        builder.HasIndex(d => new { d.PortalId, d.ModuleDefinitionId, d.PropertyName })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ProfilePropertyDefinition");

        // 04.00.04 (= 03.02.03) - CREATE INDEX IX_ProfilePropertyDefinition_PropertyName ON
        // ProfilePropertyDefinition(PropertyName ASC).
        builder.HasIndex(d => d.PropertyName)
            .HasDatabaseName("IX_ProfilePropertyDefinition_PropertyName");

        // NOT FOR REPLICATION and WITH NOCHECK have no counterpart in the model and are left unrepresented
        // across this folder. The key reuses the column mapped above, so no shadow property appears.
        builder.HasOne(d => d.Portal)
            .WithMany()
            .HasForeignKey(d => d.PortalId)
            .HasConstraintName("FK_ProfilePropertyDefinition_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // There is NO physical foreign key from this table to ModuleDefinitions anywhere in the terminal
        // schema, and the model says so rather than assuming otherwise.
        builder.HasOne(d => d.ModuleDefinition)
            .WithMany(m => m.ProfilePropertyDefinitions)
            .HasForeignKey(d => d.ModuleDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
