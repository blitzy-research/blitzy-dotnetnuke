using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="ModulePermission"/> entity to the legacy <c>dbo.ModulePermission</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table name is singular. Each row is one grant: a module, a permission from the catalogue, a
/// subject, and a flag saying whether the subject is allowed or denied. A denial suppresses the key
/// even when another row allows it, which is the legacy precedence and is evaluated in the
/// repository rather than here.
/// </para>
/// <para>
/// The subject columns tell a migration story that must be read carefully. <c>RoleID</c> was
/// originally <c>NOT NULL</c>; the 04.05.00 script copied it to a temporary column, dropped it, and
/// re-added it as <c>NULL</c>, then added a nullable <c>UserID</c> alongside. The terminal table
/// therefore grants to a role or to a single account, and both columns are nullable. Mapping either
/// as required would reject rows the legacy application writes routinely.
/// </para>
/// <para>
/// Only the account reference has a foreign key, and it carries no <c>ON DELETE</c> clause. There is
/// no foreign key to <c>Roles</c> at all. Both relationships are therefore mapped with no delete
/// behaviour, matching what the database actually enforces. The module and permission references do
/// cascade, so removing a module or retiring a catalogue entry withdraws the grants that named it.
/// </para>
/// <para>
/// The unique index <c>IX_ModulePermission</c> spans the module, the permission and both subject
/// columns. Because a unique constraint in this engine treats nulls as equal, that permits exactly
/// one subject-less grant per module and permission pair.
/// </para>
/// </remarks>
internal sealed class ModulePermissionConfiguration : IEntityTypeConfiguration<ModulePermission>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the module grant entity type.</param>
    public void Configure(EntityTypeBuilder<ModulePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ModulePermission", "dbo");

        builder.HasKey(g => g.ModulePermissionId).HasName("PK_ModulePermission");

        builder.Property(g => g.ModulePermissionId)
            .HasColumnName("ModulePermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(g => g.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(g => g.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(g => g.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int");

        builder.Property(g => g.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int");

        builder.Property(g => g.AllowAccess)
            .HasColumnName("AllowAccess")
            .HasColumnType("bit")
            .IsRequired();

        builder.HasOne(g => g.Module)
            .WithMany(m => m.ModulePermissions)
            .HasForeignKey(g => g.ModuleId)
            .HasConstraintName("FK_ModulePermission_Modules")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.Permission)
            .WithMany(p => p.ModulePermissions)
            .HasForeignKey(g => g.PermissionId)
            .HasConstraintName("FK_ModulePermission_Permission")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.Role)
            .WithMany(r => r.ModulePermissions)
            .HasForeignKey(g => g.RoleId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(g => g.User)
            .WithMany(u => u.ModulePermissions)
            .HasForeignKey(g => g.UserId)
            .HasConstraintName("FK_ModulePermissionUsers")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(g => new { g.ModuleId, g.PermissionId, g.RoleId, g.UserId })
            .IsUnique()
            .HasDatabaseName("IX_ModulePermission");
    }
}
