using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="PortalAlias"/> entity to the legacy <c>dbo.PortalAlias</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table name is singular in the schema even though the entity models a collection member, so
/// the mapping is stated explicitly rather than left to the pluralising convention.
/// </para>
/// <para>
/// The alias column is <c>HTTPAlias</c> and carries a unique non-clustered index named
/// <c>IX_PortalAlias</c>. That uniqueness is what makes exact-match tenant resolution correct: the
/// legacy resolution procedure matched with <c>PortalAlias like '%' + @PortalAlias + '%'</c> and
/// took the minimum portal identifier, which resolves an alias that is a substring of another
/// tenant's alias to the wrong tenant. The replacement middleware matches exactly, and that
/// deliberate behavioural difference is recorded in the migration notes.
/// </para>
/// <para>
/// The foreign key to <c>Portals</c> cascades on delete in the schema, so removing a tenant removes
/// its aliases. The cascade is declared here so the tracked graph behaves the same way in memory as
/// it does in the database.
/// </para>
/// </remarks>
internal sealed class PortalAliasConfiguration : IEntityTypeConfiguration<PortalAlias>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the portal alias entity type.</param>
    public void Configure(EntityTypeBuilder<PortalAlias> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PortalAlias", "dbo");

        builder.HasKey(a => a.PortalAliasId).HasName("PK_PortalAlias");

        builder.Property(a => a.PortalAliasId)
            .HasColumnName("PortalAliasID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(a => a.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(a => a.HttpAlias)
            .HasColumnName("HTTPAlias")
            .HasMaxLength(200)
            .IsRequired();

        builder.HasOne(a => a.Portal)
            .WithMany(p => p.PortalAliases)
            .HasForeignKey(a => a.PortalId)
            .HasConstraintName("FK_PortalAlias_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.HttpAlias)
            .IsUnique()
            .HasDatabaseName("IX_PortalAlias");
    }
}
