using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="Module"/> to the existing, unaltered DotNetNuke 4.9 SQL Server table <c>dbo.Modules</c>
/// through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is immutable.</b> Nothing here creates, alters, drops or seeds a database object. Every
/// column is bound by an explicit name, and the key, the index and both foreign keys carry the physical
/// names the installation already has, so renaming a member of the model can never silently rename anything
/// in the database.
/// </para>
/// <para>
/// <b>The mapping targets the terminal schema</b> - the cumulative result of replaying the upgrade scripts
/// under <c>Website/Providers/DataProviders/SqlDataProvider/</c> in version order. For this table the
/// distinction is decisive rather than pedantic, because it is the most heavily rewritten table in the
/// in-scope schema.
/// </para>
/// </remarks>
internal sealed class ModuleConfiguration : IEntityTypeConfiguration<Module>
{
    /// <summary>Applies the terminal-schema mapping for <see cref="Module"/>.</summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Module> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Modules", "dbo");

        // 01.00.00 - PK_Modules PRIMARY KEY NONCLUSTERED (ModuleID). NONCLUSTERED is expressed rather than
        // merely recorded: the SQL Server provider defaults a primary key to CLUSTERED, so leaving the
        // declaration bare would describe a physical topology this table does not have.
        builder.HasKey(m => m.ModuleId)
            .HasName("PK_Modules")
            .IsClustered(false);

        // 01.00.00 - ModuleID int IDENTITY (0, 1) NOT NULL. The seed of ZERO means the first module of a
        // fresh installation is identified by 0, so zero is a legitimate, persisted module identifier.
        builder.Property(m => m.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // MIGRATION: columns the table carried earlier in the upgrade chain but no longer carries in the
        // terminal schema are deliberately left unmapped.

        // The property is spelled ModuleDefinitionId in full, but the column keeps the abbreviated legacy
        // spelling ModuleDefID (01.00.00).
        builder.Property(m => m.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .IsRequired();

        // Null is MEANINGFUL here rather than unknown - a module with no tenant is owned by the
        // installation itself - and it is modelled as a nullable integer rather than as a required integer
        // carrying the legacy absent-integer marker of -1.
        builder.Property(m => m.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // 01.00.00 - ModuleTitle nvarchar (256) NULL: the only variable-width text column on the table, and
        // so the only one carrying a length.
        builder.Property(m => m.ModuleTitle)
            .HasColumnName("ModuleTitle")
            .HasMaxLength(256);

        // 01.00.04 adds AllTabs bit NOT NULL CONSTRAINT DF_Modules_AllTabs DEFAULT 0, and 03.01.01 restates
        // the column and re-creates that default.
        builder.Property(m => m.AllTabs)
            .HasColumnName("AllTabs")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // 02.00.00 adds IsDeleted bit NOT NULL CONSTRAINT DF_Modules_IsDeleted DEFAULT 0, restated by
        // 03.01.01 with its default re-created.
        builder.Property(m => m.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // 02.02.00 - InheritViewPermissions bit NULL. No later script makes it NOT NULL, so nullable is the
        // terminal shape.
        builder.Property(m => m.InheritViewPermissions)
            .HasColumnName("InheritViewPermissions")
            .HasColumnType("bit");

        // The two markup columns keep the legacy large-object type verbatim, because the schema is
        // immutable under this migration and widening the column to a modern large-object type would be a
        // schema change this work does not make.
        builder.Property(m => m.Header)
            .HasColumnName("Header")
            .HasColumnType("ntext");

        builder.Property(m => m.Footer)
            .HasColumnName("Footer")
            .HasColumnType("ntext");

        // Both date-boundary columns are the legacy fixed-length date type, never its modern successor.
        builder.Property(m => m.StartDate)
            .HasColumnName("StartDate")
            .HasColumnType("datetime");

        builder.Property(m => m.EndDate)
            .HasColumnName("EndDate")
            .HasColumnType("datetime");

        // Index lifecycle, read from the whole chain rather than from any single script: 01.00.10 drops any
        // earlier index of this name and creates IX_Modules over ModuleDefID; 01.00.10 creates a second
        // index over the page key; 03.00.01 drops that second index, which is never recreated, because the
        // column it covered was itself dropped three lines later; 03.00.09 drops IX_Modules and recreates
        // it as CREATE NONCLUSTERED INDEX IX_Modules ON Modules (ModuleDefID).
        builder.HasIndex(m => m.ModuleDefinitionId)
            .HasDatabaseName("IX_Modules");

        // 01.00.00 - CONSTRAINT FK_Modules_ModuleDefinitions FOREIGN KEY (ModuleDefID) REFERENCES
        // dbo.ModuleDefinitions (ModuleDefID) ON DELETE CASCADE NOT FOR REPLICATION. Never dropped: the
        // only later mention is a no-op rename to its own name at 02.00.00.
        builder.HasOne(m => m.ModuleDefinition)
            .WithMany(d => d.Modules)
            .HasForeignKey(m => m.ModuleDefinitionId)
            .HasConstraintName("FK_Modules_ModuleDefinitions")
            .OnDelete(DeleteBehavior.Cascade);

        // 03.00.09 - ALTER TABLE Modules WITH NOCHECK ADD CONSTRAINT FK_Modules_Portals FOREIGN KEY
        // (PortalID) REFERENCES Portals (PortalID) NOT FOR REPLICATION. That is the terminal form: the
        // constraint was first added at 03.00.01, dropped at 03.00.09 and re-added in the shape above.
        builder.HasOne(m => m.Portal)
            .WithMany(p => p.Modules)
            .HasForeignKey(m => m.PortalId)
            .HasConstraintName("FK_Modules_Portals")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
