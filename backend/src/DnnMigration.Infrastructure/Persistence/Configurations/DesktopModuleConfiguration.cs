using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="DesktopModule"/> to the existing, unaltered DotNetNuke 4.9 SQL Server table
/// <c>dbo.DesktopModules</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is immutable.</b> Nothing here creates, alters, drops or seeds a database object. Every
/// column is bound by an explicit name and every index by an explicit database name, so renaming a member
/// of the model can never silently rename anything in the database.
/// </para>
/// <para>
/// <b>No relationship is declared here, deliberately.</b> Three foreign keys name this table, and every one
/// of them lives on the other table; <c>DesktopModules</c> carries no key column of its own, so both
/// navigations on the entity are inverse ends. Each relationship must be declared exactly once, by the
/// configuration of the entity that owns the key column.
/// </para>
/// </remarks>
internal sealed class DesktopModuleConfiguration : IEntityTypeConfiguration<DesktopModule>
{
    /// <summary>Applies the terminal-schema mapping for <see cref="DesktopModule"/>.</summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<DesktopModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DesktopModules", "dbo");

        // 02.00.00:L5151-5154 - PK_{objectQualifier}DesktopModules PRIMARY KEY CLUSTERED (DesktopModuleID).
        // Named explicitly so the model carries the constraint name the database already has rather than
        // one invented by convention.
        builder.HasKey(d => d.DesktopModuleId)
            .HasName("PK_DesktopModules");

        // Absence is expressed with nullable CLR types, never with the legacy sentinels (-1 for an integer,
        // 255 for a byte, MinValue for the floating-point, decimal and date types, and the empty string for
        // text).

        // 02.00.00:L5141 - [DesktopModuleID] [int] IDENTITY (1, 1) NOT NULL. The seed is recorded so
        // a model-generated script would continue the existing sequence rather than restart it.
        builder.Property(d => d.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(d => d.FriendlyName)
            .HasColumnName("FriendlyName")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(d => d.Description)
            .HasColumnName("Description")
            .HasMaxLength(2000);

        // 02.00.00:L5144 - [Version] [nvarchar] (8) NULL. Held as text because the scripts write and
        // compare the zero-padded legacy form, for example Version = '03.01.00'.
        builder.Property(d => d.Version)
            .HasColumnName("Version")
            .HasMaxLength(8);

        // 02.00.00:L5145-5146 - [IsPremium] and [IsAdmin], both [bit] NOT NULL. The terminal schema
        // attaches no default constraint to either, so none is declared: inventing one would let an insert
        // that omits the value succeed here and fail against the real database.
        builder.Property(d => d.IsPremium)
            .HasColumnName("IsPremium")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(d => d.IsAdmin)
            .HasColumnName("IsAdmin")
            .HasColumnType("bit")
            .IsRequired();

        // 02.02.02:L116-117 - ALTER TABLE ... ADD BusinessControllerClass nvarchar (200). The script
        // states no nullability, so the column takes the permissive default and this matches it.
        builder.Property(d => d.BusinessControllerClass)
            .HasColumnName("BusinessControllerClass")
            .HasMaxLength(200);

        // 03.01.00: L12 adds [FolderName] nvarchar(128) NULL, L18 back-fills it from FriendlyName and
        // L22-23 promotes it to NOT NULL. The terminal state is mapped, so this is required; mapping the
        // historical add would let a null reach a column that rejects it.
        builder.Property(d => d.FolderName)
            .HasColumnName("FolderName")
            .HasMaxLength(128)
            .IsRequired();

        // 03.01.00: L13 adds [ModuleName] nvarchar(128) NULL, L19 back-fills it from FriendlyName
        // and L26-27 promotes it to NOT NULL. Required for the same reason.
        builder.Property(d => d.ModuleName)
            .HasColumnName("ModuleName")
            .HasMaxLength(128)
            .IsRequired();

        // 03.01.00:L14 adds [SupportedFeatures] int NOT NULL defaulting to 0, and 03.01.01:L912-914
        // re-asserts NOT NULL and re-creates that default after dropping it by name.
        builder.Property(d => d.SupportedFeatures)
            .HasColumnName("SupportedFeatures")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.CompatibleVersions)
            .HasColumnName("CompatibleVersions")
            .HasMaxLength(500);

        builder.Property(d => d.Dependencies)
            .HasColumnName("Dependencies")
            .HasMaxLength(400);

        // 04.05.00:L970-972 - guarded ALTER TABLE ... ADD Permissions nvarchar(400) NULL. Despite the name
        // this is installer metadata a package declares about itself, unrelated to the portal permission
        // model.
        builder.Property(d => d.Permissions)
            .HasColumnName("Permissions")
            .HasMaxLength(400);

        builder.Ignore(d => d.IsPortable);
        builder.Ignore(d => d.IsSearchable);
        builder.Ignore(d => d.IsUpgradeable);

        // Uniqueness on this table is asymmetric and measured. 03.01.00:L30-31 adds CONSTRAINT
        // IX_{objectQualifier}DesktopModules_ModuleName UNIQUE NONCLUSTERED (ModuleName) - the only
        // uniqueness the terminal schema imposes, and what makes ModuleName the natural key the legacy
        // GetDesktopModuleByModuleName query relies on.
        builder.HasIndex(d => d.ModuleName)
            .IsUnique()
            .HasDatabaseName("IX_DesktopModules_ModuleName");

        builder.HasIndex(d => d.FriendlyName)
            .HasDatabaseName("IX_DesktopModules_FriendlyName");
    }
}
