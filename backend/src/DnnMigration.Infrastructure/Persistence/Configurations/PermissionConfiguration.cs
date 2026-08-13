using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="Permission"/> to the existing, immutable DotNetNuke 4.9 SQL Server table
/// <c>dbo.Permission</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table name is singular.</b> The terminal schema declares <c>Permission</c>, not <c>Permission</c>
/// pluralised, so the mapping states the name explicitly and the provider's pluralising convention is
/// deliberately overridden. Three of the four tables in this family are singular, which is why the name is
/// asserted per table here rather than assumed folder-wide.
/// </para>
/// <para>
/// <b>Every text column on this table is ANSI, and that is unusual.</b> All three are declared
/// <c>varchar(50)</c> in the terminal schema, never the Unicode counterpart. The provider maps a string
/// property to the Unicode type by default, so each of the three says otherwise explicitly.
/// </para>
/// </remarks>
internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    /// <summary>Applies the mapping for the permission catalogue entity type.</summary>
    /// <param name="builder">The builder for the permission catalogue entity type.</param>
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // How the citations below were obtained, recorded so they can be re-checked rather than trusted.

        // SINGULAR legacy table name. CREATE TABLE Permission is declared once in the whole chain, at
        // 02.02.00:L684, and the provider's pluralising convention would look for a table that does not
        // exist.
        builder.ToTable("Permission", "dbo");

        // Primary-key lineage. Added as PK_Permission PRIMARY KEY CLUSTERED over PermissionID at
        // 02.02.00:L723-L727, dropped at 03.00.09:L463 and recreated clustered at 03.00.09:L475 - the
        // terminal form.
        builder.HasKey(p => p.PermissionId)
            .HasName("PK_Permission");

        // PermissionID int IDENTITY (1, 1) NOT NULL at 02.02.00:L685. The column keeps the legacy
        // upper-case ID spelling.
        builder.Property(p => p.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // PermissionCode varchar (50) NOT NULL at 02.02.00:L686, never re-typed by any later script.
        builder.Property(p => p.PermissionCode)
            .HasColumnName("PermissionCode")
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        // The property is spelled ModuleDefinitionId in full, but the column keeps the abbreviated legacy
        // spelling ModuleDefID, declared int NOT NULL at 02.02.00:L687.
        builder.Property(p => p.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .IsRequired();

        // The conversion contract for this column, which is the single most consequential decision in this
        // file.
        builder.Property(p => p.PermissionKey)
            .HasColumnName("PermissionKey")
            .HasConversion<string>()
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        // PermissionName varchar (50) NOT NULL at 02.02.00:L689, never re-typed. ANSI for the same reason
        // as PermissionCode above.
        builder.Property(p => p.PermissionName)
            .HasColumnName("PermissionName")
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        // The terminal object set for this table is exactly two objects, and this index is the second of
        // them.
        builder.HasIndex(p => new { p.PermissionCode, p.ModuleDefinitionId, p.PermissionKey })
            .IsUnique()
            .HasDatabaseName("IX_Permission");
    }
}
