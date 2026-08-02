using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="RoleGroup"/> entity to the legacy <c>dbo.RoleGroups</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Role groups arrived at 03.02.03 and are purely organisational: they gather roles for
/// presentation and carry no permissions of their own.
/// </para>
/// <para>
/// The identity seed is <c>0</c>, matching the roles table, so group zero is a genuine group.
/// Unlike a role, a group's tenant reference is required — there is no host-level group — and its
/// foreign key cascades, so removing a tenant removes its groups. The roles that pointed at those
/// groups are not removed with them, because the role's own reference to a group carries no cascade.
/// </para>
/// <para>
/// The unique index <c>IX_RoleGroupName</c> spans the tenant and the group name together.
/// </para>
/// </remarks>
internal sealed class RoleGroupConfiguration : IEntityTypeConfiguration<RoleGroup>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the role group entity type.</param>
    public void Configure(EntityTypeBuilder<RoleGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RoleGroups", "dbo");

        builder.HasKey(g => g.RoleGroupId).HasName("PK_RoleGroups");

        builder.Property(g => g.RoleGroupId)
            .HasColumnName("RoleGroupID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        builder.Property(g => g.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(g => g.RoleGroupName)
            .HasColumnName("RoleGroupName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(g => g.Description)
            .HasColumnName("Description")
            .HasMaxLength(1000);

        builder.HasOne(g => g.Portal)
            .WithMany()
            .HasForeignKey(g => g.PortalId)
            .HasConstraintName("FK_RoleGroups_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => new { g.PortalId, g.RoleGroupName })
            .IsUnique()
            .HasDatabaseName("IX_RoleGroupName");
    }
}
