using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="PortalDesktopModule"/> join entity to the existing, immutable DotNetNuke 4.9 table
/// <c>dbo.PortalDesktopModules</c>.
/// </summary>
/// <remarks>
/// <para>
/// One row is one grant: it entitles a single portal to use a single installed module package. A grant
/// holds no state beyond the pair it joins, so this mapping is short - and every line of it is
/// load-bearing, because none of the three column names, none of the four constraint names and neither
/// delete behaviour can be reached by convention.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything: the
/// mapping describes a table that already exists so that Entity Framework can address it, and the baseline
/// migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// </remarks>
internal sealed class PortalDesktopModuleConfiguration : IEntityTypeConfiguration<PortalDesktopModule>
{
    /// <summary>Applies the mapping for the portal-to-package grant entity type.</summary>
    /// <param name="builder">The builder for the grant entity type.</param>
    public void Configure(EntityTypeBuilder<PortalDesktopModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Neither of those two names is mapped, and neither may ever be added as a scalar, because no such
        // column exists on this table and a mapping for one would fail on the first read of a real
        // installation. The domain entity declares neither, so there is nothing to exclude either.

        // The legacy provider registration runs with an empty object qualifier
        // [Website/release.config:L354] and "dbo" as the database owner [:L355], so the physical name is
        // unqualified and the schema is dbo.
        builder.ToTable("PortalDesktopModules", "dbo");

        // PK_PortalDesktopModules PRIMARY KEY CLUSTERED (PortalDesktopModuleID) [02.02.02:L3036-L3040]. The
        // legacy name is preserved so that the constraint the model describes is the constraint the live
        // database already has.
        builder.HasKey(g => g.PortalDesktopModuleId).HasName("PK_PortalDesktopModules");

        // The database assigns this value: the legacy insert supplies only the two foreign keys and then
        // returns SCOPE_IDENTITY() [02.02.02:L3111-L3127].
        builder.Property(g => g.PortalDesktopModuleId)
            .HasColumnName("PortalDesktopModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // All three columns are NOT NULL [02.02.02:L3030-L3032], so no nullable-sentinel question arises on
        // this table at all.
        builder.Property(g => g.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(g => g.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .IsRequired();

        // ALTER TABLE PortalDesktopModules ADD CONSTRAINT IX_PortalDesktopModules UNIQUE NONCLUSTERED
        // (PortalID, DesktopModuleID) [02.02.02:L3044-L3049], never dropped by any upgrade script.
        builder.HasIndex(g => new { g.PortalId, g.DesktopModuleId })
            .IsUnique()
            .HasDatabaseName("IX_PortalDesktopModules");

        // Both terminal foreign keys are declared ON DELETE CASCADE and NOT FOR REPLICATION - to
        // dbo.DesktopModules at [02.02.02:L3053-L3061] and to dbo.Portals at [02.02.02:L3065-L3073] - and
        // both cascades are reproduced faithfully under their legacy constraint names.
        builder.HasOne(g => g.Portal)
            .WithMany(portal => portal.PortalDesktopModules)
            .HasForeignKey(g => g.PortalId)
            .HasConstraintName("FK_PortalDesktopModules_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.DesktopModule)
            .WithMany(package => package.PortalDesktopModules)
            .HasForeignKey(g => g.DesktopModuleId)
            .HasConstraintName("FK_PortalDesktopModules_DesktopModules")
            .OnDelete(DeleteBehavior.Cascade);

        // Nothing further is declared.
    }
}
