using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="PortalDesktopModule"/> join entity to the legacy
/// <c>dbo.PortalDesktopModules</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table grants a tenant the right to use a registered module package. It carries a surrogate
/// identity of its own rather than a composite key, plus a unique non-clustered index over the pair
/// it joins, so the same package cannot be granted twice to one tenant.
/// </para>
/// <para>
/// Both foreign keys cascade on delete in the schema. The two cascade paths do not converge on a
/// single principal, so they are declared exactly as the schema has them: removing a tenant or
/// removing a package withdraws the grants that referenced it.
/// </para>
/// </remarks>
internal sealed class PortalDesktopModuleConfiguration : IEntityTypeConfiguration<PortalDesktopModule>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the portal-to-package grant entity type.</param>
    public void Configure(EntityTypeBuilder<PortalDesktopModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PortalDesktopModules", "dbo");

        builder.HasKey(g => g.PortalDesktopModuleId).HasName("PK_PortalDesktopModules");

        builder.Property(g => g.PortalDesktopModuleId)
            .HasColumnName("PortalDesktopModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(g => g.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(g => g.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .IsRequired();

        builder.HasOne(g => g.Portal)
            .WithMany(p => p.PortalDesktopModules)
            .HasForeignKey(g => g.PortalId)
            .HasConstraintName("FK_PortalDesktopModules_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.DesktopModule)
            .WithMany(d => d.PortalDesktopModules)
            .HasForeignKey(g => g.DesktopModuleId)
            .HasConstraintName("FK_PortalDesktopModules_DesktopModules")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => new { g.PortalId, g.DesktopModuleId })
            .IsUnique()
            .HasDatabaseName("IX_PortalDesktopModules");
    }
}
