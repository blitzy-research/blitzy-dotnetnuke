using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Permission"/> entity to the legacy <c>dbo.Permission</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table name is singular. It is the catalogue of permission keys an installation recognises,
/// not a grant: a grant is a row in <c>ModulePermission</c> or <c>TabPermission</c> that points here.
/// </para>
/// <para>
/// All four text columns are <c>varchar</c> rather than <c>nvarchar</c>, so they are declared
/// non-Unicode. Declaring them as Unicode would prevent the unique index from being used for lookup
/// and would change comparison behaviour under a case-sensitive collation.
/// </para>
/// <para>
/// <c>PermissionCode</c> is free text and deliberately not modelled as a closed set: an installation
/// that carries a code this migration has never seen must round-trip it intact, which is why the
/// entity holds it as a string. <c>PermissionKey</c> is likewise a string on the entity even though a
/// small enumeration of the well-known keys exists, and for the same reason.
/// </para>
/// <para>
/// The unique index <c>IX_Permission</c> spans the code, the owning definition and the key together,
/// which is what allows the same key — <c>VIEW</c>, say — to exist once per scope code per
/// definition.
/// </para>
/// <para>
/// <c>ModuleDefID</c> is required but has no foreign key anywhere in the upgrade chain, and the
/// legacy data uses a negative value for a permission that belongs to no particular definition. The
/// relationship is modelled so the real column is used rather than a shadow one, with no delete
/// behaviour attached: the database enforces nothing here, and a sentinel-valued reference has no
/// principal row to cascade from.
/// </para>
/// </remarks>
internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the permission catalogue entity type.</param>
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Permission", "dbo");

        builder.HasKey(p => p.PermissionId).HasName("PK_Permission");

        builder.Property(p => p.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(p => p.PermissionCode)
            .HasColumnName("PermissionCode")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(p => p.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.PermissionKey)
            .HasColumnName("PermissionKey")
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(p => p.PermissionName)
            .HasColumnName("PermissionName")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        builder.HasOne(p => p.ModuleDefinition)
            .WithMany(d => d.Permissions)
            .HasForeignKey(p => p.ModuleDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(p => new { p.PermissionCode, p.ModuleDefinitionId, p.PermissionKey })
            .IsUnique()
            .HasDatabaseName("IX_Permission");
    }
}
