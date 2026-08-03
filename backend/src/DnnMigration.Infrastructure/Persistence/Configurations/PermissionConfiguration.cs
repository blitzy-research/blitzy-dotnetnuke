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
/// entity holds it as a string. <c>PermissionKey</c> takes the opposite decision - it is the closed
/// <see cref="Domain.Enums.PermissionKey"/> enumeration on the entity, converted to and from the
/// member's own name here - because that vocabulary was measured to be exhaustive for this
/// DotNetNuke generation and the four names are load-bearing data the database already holds.
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

        // MIGRATION: the entity holds the closed PermissionKey enumeration, whose members carry no
        // explicit values, so the MEMBER NAME is the persisted value and HasConversion<string>() is the
        // correct conversion here. Without it the provider would store the ordinal - 0, 1, 2, 3 - into a
        // varchar column and every legacy row and legacy predicate would stop matching. This is the
        // deliberate opposite of Roles.BillingFrequency, whose enumeration carries character literals
        // and therefore needs an explicit character converter; neither approach may be cross-applied.
        //
        // MIGRATION: the width is the TERMINAL width. The column is created varchar(20) by
        // Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider line 688 and widened
        // by 04.06.00.SqlDataProvider lines 397 to 398 - "enlarge permission key field" - with
        // ALTER COLUMN PermissionKey varchar(50) not null, which also re-states the column as required.
        // Nothing later in the 88-script chain touches it, so 50 is the length to map.
        builder.Property(p => p.PermissionKey)
            .HasColumnName("PermissionKey")
            .HasConversion<string>()
            .HasMaxLength(50)
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
