using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="PortalAlias"/> entity to the existing, immutable DotNetNuke 4.9 table
/// <c>dbo.PortalAlias</c> - singular - one row of which maps a single host name to the portal that answers
/// on it.
/// </summary>
/// <remarks>
/// <para>
/// Every column name, store type, length, nullability and constraint below is taken from the
/// <em>terminal</em> state of the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, never from a single script read in isolation.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything: the
/// mapping describes a table that already exists so that Entity Framework can address it, and no
/// installation is ever reshaped by it.
/// </para>
/// </remarks>
internal sealed class PortalAliasConfiguration : IEntityTypeConfiguration<PortalAlias>
{
    /// <summary>Applies the mapping for the portal alias entity type.</summary>
    /// <param name="builder">The builder for the portal alias entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<PortalAlias> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The legacy table name is SINGULAR. Entity Framework's pluralising convention would derive a
        // pluralised name from the entity name, and every read and write against this type would then
        // address a table no DotNetNuke database contains, so the name is stated explicitly instead of
        // being left to convention.
        builder.ToTable("PortalAlias", "dbo");

        // PK_PortalAlias PRIMARY KEY CLUSTERED (PortalAliasID), added at 02.02.02:L3957-L3961. The physical
        // constraint name is preserved so that anything generated from this model names the constraint the
        // database already carries.
        builder.HasKey(a => a.PortalAliasId).HasName("PK_PortalAlias");

        // PortalAliasID int IDENTITY(1, 1) NOT NULL [02.02.02:L3805]. This identity seeds at 1, unlike
        // Portals.PortalID at IDENTITY(-1, 1) and Roles.RoleID at IDENTITY(0, 1), so stored alias keys are
        // always positive.
        builder.Property(a => a.PortalAliasId)
            .HasColumnName("PortalAliasID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // PortalID int NOT NULL [02.02.02:L3806], so an alias always belongs to a portal and the
        // relationship below is required rather than optional.
        builder.Property(a => a.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        // The column is NULLABLE and is deliberately left that way. Its terminal declaration is "HTTPAlias
        // nvarchar (200)" [02.02.02:L3807] with no NOT NULL clause, and no script in the chain adds one, so
        // the store permits null; the CLR property is string?, which matches.
        builder.Property(a => a.HttpAlias)
            .HasColumnName("HTTPAlias")
            .HasMaxLength(200);

        // FK_PortalAlias_Portals FOREIGN KEY (PortalID) REFERENCES Portals (PortalID) ON DELETE CASCADE
        // [02.02.02:L3811-L3818].
        builder.HasOne(a => a.Portal)
            .WithMany(p => p.PortalAliases)
            .HasForeignKey(a => a.PortalId)
            .HasConstraintName("FK_PortalAlias_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // IX_PortalAlias UNIQUE NONCLUSTERED (HTTPAlias), added at 03.00.07:L14-L18 and never dropped by a
        // later script.
        builder.HasIndex(a => a.HttpAlias)
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_PortalAlias");
    }
}
