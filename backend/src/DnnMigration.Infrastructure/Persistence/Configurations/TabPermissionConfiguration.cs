using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="TabPermission"/> entity to the legacy <c>dbo.TabPermission</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table name is singular. Each row grants or denies one catalogue permission on one page to one
/// subject, and a denial suppresses the key even when another row allows it.
/// </para>
/// <para>
/// These rows are also what a module inherits when its inherit-view flag is set: the view answer then
/// comes from the module's page rather than from the module's own grants, while the edit answer always
/// comes from the module. Reproducing that branch is the permission service's work, but it is only
/// possible because both grant tables are mapped with the same subject shape.
/// </para>
/// <para>
/// The subject columns underwent the same 04.05.00 rework as the module grants: <c>RoleID</c> was
/// dropped and re-added as nullable, and a nullable <c>UserID</c> was added beside it. Only the
/// account reference has a foreign key and it carries no <c>ON DELETE</c> clause; there is no foreign
/// key to <c>Roles</c>. The page and permission references cascade.
/// </para>
/// <para>
/// The page reference also explains the tab permission cache invalidation the services perform:
/// because the grants live here rather than on the page row, changing a grant must drop the page's
/// cached permission set even though the page itself is untouched.
/// </para>
/// </remarks>
internal sealed class TabPermissionConfiguration : IEntityTypeConfiguration<TabPermission>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the page grant entity type.</param>
    public void Configure(EntityTypeBuilder<TabPermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TabPermission", "dbo");

        builder.HasKey(g => g.TabPermissionId).HasName("PK_TabPermission");

        builder.Property(g => g.TabPermissionId)
            .HasColumnName("TabPermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(g => g.TabId)
            .HasColumnName("TabID")
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

        builder.HasOne(g => g.Tab)
            .WithMany(t => t.TabPermissions)
            .HasForeignKey(g => g.TabId)
            .HasConstraintName("FK_TabPermission_Tabs")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.Permission)
            .WithMany(p => p.TabPermissions)
            .HasForeignKey(g => g.PermissionId)
            .HasConstraintName("FK_TabPermission_Permission")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.Role)
            .WithMany(r => r.TabPermissions)
            .HasForeignKey(g => g.RoleId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(g => g.User)
            .WithMany(u => u.TabPermissions)
            .HasForeignKey(g => g.UserId)
            .HasConstraintName("FK_TabPermission_Users")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(g => new { g.TabId, g.PermissionId, g.RoleId, g.UserId })
            .IsUnique()
            .HasDatabaseName("IX_TabPermission");
    }
}
