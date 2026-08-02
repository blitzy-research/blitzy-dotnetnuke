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
/// Deletion is logical here: the <c>Deleted</c> flag marks a definition as withdrawn while its
/// stored values survive, which is why the repository exposes an explicit switch for including
/// withdrawn definitions rather than filtering them unconditionally.
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

        builder.Property(d => d.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(d => d.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        builder.Property(d => d.Deleted)
            .HasColumnName("Deleted")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(d => d.DataType)
            .HasColumnName("DataType")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(d => d.DefaultValue)
            .HasColumnName("DefaultValue")
            .HasMaxLength(50);

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

        builder.Property(d => d.Required)
            .HasColumnName("Required")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(d => d.ValidationExpression)
            .HasColumnName("ValidationExpression")
            .HasMaxLength(100);

        builder.Property(d => d.ViewOrder)
            .HasColumnName("ViewOrder")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(d => d.Visible)
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

        builder.HasIndex(d => new { d.PortalId, d.ModuleDefinitionId, d.PropertyName })
            .IsUnique()
            .HasDatabaseName("IX_ProfilePropertyDefinition");

        builder.HasIndex(d => d.PropertyName)
            .HasDatabaseName("IX_ProfilePropertyDefinition_PropertyName");
    }
}
