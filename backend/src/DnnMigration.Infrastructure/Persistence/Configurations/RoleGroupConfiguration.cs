using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>Binds the <see cref="RoleGroup"/> entity to the legacy <c>dbo.RoleGroups</c> table.</summary>
/// <remarks>
/// <para>
/// This table is the one welcome simplicity in an otherwise destructive upgrade chain.
/// </para>
/// <para>
/// The schema is read-only truth. Nothing here creates, alters or drops anything: this mapping describes a
/// table that already exists so that Entity Framework can address it, and the baseline migration is
/// deliberately empty so that no installation is ever reshaped.
/// </para>
/// </remarks>
internal sealed class RoleGroupConfiguration : IEntityTypeConfiguration<RoleGroup>
{
    /// <summary>Applies the mapping for the role group entity type.</summary>
    /// <param name="builder">The builder for the role group entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<RoleGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Every citation below is doubled, and that is a property of the source rather than duplicated
        // evidence.
        builder.ToTable("RoleGroups", "dbo");

        // PK_RoleGroups is declared NONCLUSTERED (04.00.04:L57-58, identical to 03.02.03:L24-25) and is
        // never dropped.
        builder.HasKey(g => g.RoleGroupId).HasName("PK_RoleGroups").IsClustered(false);

        // RoleGroupID is int IDENTITY(0,1) NOT NULL (04.00.04:L51, identical to 03.02.03:L18). THE SEED IS
        // ZERO, so RoleGroupId 0 is a real, valid, saved group and must never be read as "absent", "unset",
        // "default" or "not yet persisted".
        builder.Property(g => g.RoleGroupId)
            .HasColumnName("RoleGroupID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // PortalID is int NOT NULL on this table (04.00.04:L52, identical to 03.02.03:L19), which DIVERGES
        // from its immediate neighbours: Roles.PortalID and Tabs.PortalID are both int NULL, because a role
        // and a page may exist at host level with no owning tenant.
        builder.Property(g => g.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar of width 50, NOT NULL (04.00.04:L53). Unicode and variable width: the store type is
        // neither narrowed to ANSI nor declared fixed length, because this table has no ANSI and no
        // fixed-width column to narrow it to.
        builder.Property(g => g.RoleGroupName)
            .HasColumnName("RoleGroupName")
            .HasMaxLength(50)
            .IsRequired();

        // nvarchar of width 1000, NULL (04.00.04:L54) - the only nullable column of the table, so this is
        // the only property left optional. One thousand characters, matching Roles.Description, not the
        // five hundred of Portals.Description and Tabs.Description.
        builder.Property(g => g.Description)
            .HasColumnName("Description")
            .HasMaxLength(1000);

        // The uniqueness rule is declared as a UNIQUE NONCLUSTERED table CONSTRAINT rather than a CREATE
        // UNIQUE INDEX statement (04.00.04:L60-61, identical to 03.02.03:L27-28).
        builder.HasIndex(g => new { g.PortalId, g.RoleGroupName })
            .IsUnique()
            .HasDatabaseName("IX_RoleGroupName");

        // The inverse below is deliberately left unspecified: the Portal entity declares collection
        // navigations for aliases, modules, tabs, roles, user portals and portal desktop modules, but none
        // for role groups.
        builder.HasOne(g => g.Portal)
            .WithMany()
            .HasForeignKey(g => g.PortalId)
            .HasConstraintName("FK_RoleGroups_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: the inbound relationship from the Roles table is deliberately ABSENT from this file.
    }
}
