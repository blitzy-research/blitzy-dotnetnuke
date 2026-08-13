using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="ModuleControl"/> to the terminal legacy table <c>dbo.ModuleControls</c>, the row set
/// recording which user-interface entry points a module definition publishes.
/// </summary>
/// <remarks>
/// <para>
/// Every binding below is measured against the cumulative terminal state of the eighty-eight upgrade
/// scripts under <c>Website/Providers/DataProviders/SqlDataProvider/</c> replayed in order, never against
/// the table as first created. That distinction is load-bearing here, because the table is created with
/// eight columns, later gains two more, and has one of the original eight widened.
/// </para>
/// <para>
/// The legacy schema is immutable for this migration. Nothing here creates, alters or seeds anything: the
/// table, every column, the key, the unique constraint and the cascading foreign key all already exist, and
/// this type only describes them so the model addresses them correctly.
/// </para>
/// </remarks>
internal sealed class ModuleControlConfiguration : IEntityTypeConfiguration<ModuleControl>
{
    /// <summary>Applies the terminal-schema mapping for <see cref="ModuleControl"/>.</summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<ModuleControl> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ModuleControls", "dbo");

        // 02.00.00:L5009-5013 - CONSTRAINT PK_ModuleControls PRIMARY KEY CLUSTERED (ModuleControlID), the
        // name at line 5010. Named explicitly so the model carries the constraint name the database already
        // has rather than one invented by convention.
        builder.HasKey(c => c.ModuleControlId)
            .HasName("PK_ModuleControls");

        // 02.00.00:L4997 - ModuleControlID int IDENTITY (1, 1) NOT NULL. The seed is recorded so a
        // model-generated script would continue the existing sequence rather than restart it.
        builder.Property(c => c.ModuleControlId)
            .HasColumnName("ModuleControlID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // 02.00.00:L4998 - ModuleDefID int NULL. Nullable, and deliberately not marked required: a control
        // need belong to no definition, and 02.02.00:L544 compares this column with an explicit "is null"
        // arm precisely because the database can hold no value there.
        builder.Property(c => c.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        // The terminal width of ControlKey is fifty, not the twenty it was created with. 02.00.00:L4999
        // declares ControlKey nvarchar (20) NULL and 02.02.00:L459-460 then widens it with "ALTER TABLE
        // ModuleControls ALTER COLUMN ControlKey [nvarchar] (50)", which the same script's
        // UpdateModuleControl procedure restates as its parameter width at line 467.
        builder.Property(c => c.ControlKey)
            .HasColumnName("ControlKey")
            .HasMaxLength(50);

        // 02.00.00:L5000 - ControlTitle nvarchar (50) NULL. Unicode is the default for a string, so it
        // is not restated; this table declares no non-Unicode text column.
        builder.Property(c => c.ControlTitle)
            .HasColumnName("ControlTitle")
            .HasMaxLength(50);

        // 02.00.00:L5001 - ControlSrc nvarchar (256) NULL. One of the three columns in the unique
        // constraint, so the stored value is mapped as it stands and never normalised.
        builder.Property(c => c.ControlSrc)
            .HasColumnName("ControlSrc")
            .HasMaxLength(256);

        // 02.00.00:L5002 - IconFile nvarchar (100) NULL.
        builder.Property(c => c.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // 02.00.00:L5003 - ControlType int NOT NULL, with no default constraint.
        builder.Property(c => c.ControlType)
            .HasColumnName("ControlType")
            .HasColumnType("int")
            .IsRequired();

        // 02.00.00:L5004 - ViewOrder int NULL. The ordering this column drives belongs to the query
        // that reads it, at 02.02.00:L546, and not to the mapping.
        builder.Property(c => c.ViewOrder)
            .HasColumnName("ViewOrder")
            .HasColumnType("int");

        // 02.02.00:L454-455 - ALTER TABLE ModuleControls ADD HelpUrl [nvarchar] (200) NULL. Absent when the
        // table was created. The SQL identifier is itself spelled HelpUrl, so the property name and the
        // column name agree and this binding is an identity mapping.
        builder.Property(c => c.HelpUrl)
            .HasColumnName("HelpUrl")
            .HasMaxLength(200);

        // 04.05.00:L1258-1260 - ADD SupportsPartialRendering bit NOT NULL CONSTRAINT
        // DF_ModuleControls_SupportsPartialRendering DEFAULT 0, guarded by a COLUMNPROPERTY test so the
        // upgrade is repeatable. Likewise absent at creation.
        builder.Property(c => c.SupportsPartialRendering)
            .HasColumnName("SupportsPartialRendering")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // 02.00.00:L5025-5031 - CONSTRAINT FK_ModuleControls_ModuleDefinitions FOREIGN KEY (ModuleDefID)
        // REFERENCES ModuleDefinitions (ModuleDefID) ON DELETE CASCADE NOT FOR REPLICATION. The cascade is
        // declared by the database, so it is reproduced rather than softened, and it is not invented
        // anywhere the database does not state one.
        builder.HasOne(c => c.ModuleDefinition)
            .WithMany(d => d.ModuleControls)
            .HasForeignKey(c => c.ModuleDefinitionId)
            .HasConstraintName("FK_ModuleControls_ModuleDefinitions")
            .OnDelete(DeleteBehavior.Cascade);

        // 02.00.00:L5016-5022 - CONSTRAINT IX_ModuleControls UNIQUE NONCLUSTERED (ModuleDefID, ControlKey,
        // ControlSrc), the name at line 5017. Never dropped by any upgrade script.
        builder.HasIndex(c => new { c.ModuleDefinitionId, c.ControlKey, c.ControlSrc })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ModuleControls");
    }
}
