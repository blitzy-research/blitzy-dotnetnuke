using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="ModuleDefinition"/> to the existing, unaltered DotNetNuke 4.9 SQL Server table
/// <c>dbo.ModuleDefinitions</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is immutable.</b> Nothing here creates, alters, drops or seeds a database object. Every
/// column is bound by an explicit name and every index by an explicit database name, so renaming a member
/// of the model can never silently rename anything in the database.
/// </para>
/// <para>
/// <b>The mapping targets the terminal schema</b> - the cumulative result of replaying all eighty-eight
/// upgrade scripts under <c>Website/Providers/DataProviders/SqlDataProvider/</c> in version order. That
/// history is destructive as well as additive, which makes the baseline script actively misleading for this
/// table in particular: it declares seven columns of which only two survive.
/// </para>
/// </remarks>
internal sealed class ModuleDefinitionConfiguration : IEntityTypeConfiguration<ModuleDefinition>
{
    /// <summary>Applies the terminal-schema mapping for <see cref="ModuleDefinition"/>.</summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<ModuleDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ModuleDefinitions", "dbo");

        // 01.00.00:L462-467 - CONSTRAINT PK_ModuleDefinitions PRIMARY KEY NONCLUSTERED (ModuleDefID), the
        // name at line 464. Named explicitly so the model carries the constraint name the database already
        // has rather than one invented by convention.
        builder.HasKey(m => m.ModuleDefinitionId)
            .HasName("PK_ModuleDefinitions")
            .IsClustered(false);

        // The property is spelled ModuleDefinitionId in full, but the column keeps the abbreviated legacy
        // spelling ModuleDefID (01.00.00:L66).
        builder.Property(m => m.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // 01.00.00:L67 - FriendlyName nvarchar (128) NOT NULL. Unicode is the default for a string,
        // so it is not restated; this is the only text column on the table.
        builder.Property(m => m.FriendlyName)
            .HasColumnName("FriendlyName")
            .HasMaxLength(128)
            .IsRequired();

        // 02.00.00:L5173-5174 adds DesktopModuleID int NOT NULL together with the default constraint
        // DF_ModuleDefinitions_DesktopModuleID DEFAULT 0, and L5242-5243 of that same script drops that
        // constraint again once its back-fill loop has run.
        builder.Property(m => m.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .IsRequired();

        // 03.01.00:L297-298 adds DefaultCacheTime int NOT NULL with DEFAULT 0, and 03.01.01 restates it:
        // L991-999 drops the default constraint by looking its name up dynamically, L1001 re-asserts the
        // column as NOT NULL and L1003 re-creates the default.
        builder.Property(m => m.DefaultCacheTime)
            .HasColumnName("DefaultCacheTime")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // Uniqueness here is the mirror image of the sibling table's, and the two must not be conflated.
        // 01.00.08:L5867-5872 adds CONSTRAINT IX_ModuleDefinitions UNIQUE NONCLUSTERED (FriendlyName) to
        // this table and no later script drops it, so a friendly name is unique across every definition in
        // the installation - which is what makes it the natural key that GetModuleDefinitionByName resolves
        // against.
        builder.HasIndex(m => m.FriendlyName)
            .IsUnique()
            .HasDatabaseName("IX_ModuleDefinitions");

        // 02.00.00:L5269-5270 - create index IX_ModuleDefinitions_1 on ModuleDefinitions (DesktopModuleID).
        // Deliberately not unique: one package publishes many definitions, which is the entire point of the
        // 02.00.00 split.
        builder.HasIndex(m => m.DesktopModuleId)
            .HasDatabaseName("IX_ModuleDefinitions_1");

        // 02.00.00:L5258-5265 - CONSTRAINT FK_ModuleDefinitions_DesktopModules FOREIGN KEY
        // (DesktopModuleID) REFERENCES DesktopModules (DesktopModuleID) ON DELETE CASCADE NOT FOR
        // REPLICATION. The cascade is described as the existing schema declares it rather than softened:
        // deleting a package already removes its definitions in the legacy database.
        builder.HasOne(m => m.DesktopModule)
            .WithMany(d => d.ModuleDefinitions)
            .HasForeignKey(m => m.DesktopModuleId)
            .HasConstraintName("FK_ModuleDefinitions_DesktopModules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
