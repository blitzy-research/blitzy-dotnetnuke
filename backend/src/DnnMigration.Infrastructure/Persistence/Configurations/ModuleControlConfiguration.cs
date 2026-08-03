using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="ModuleControl"/> to the terminal legacy table <c>dbo.ModuleControls</c>, the row
/// set recording which user-interface entry points a module definition publishes.
/// </summary>
/// <remarks>
/// <para>
/// Every binding below is measured against the cumulative terminal state of the eighty-eight upgrade
/// scripts under <c>Website/Providers/DataProviders/SqlDataProvider/</c> replayed in order, never
/// against the table as first created. That distinction is load-bearing here, because the table is
/// created with eight columns, later gains two more, and has one of the original eight widened.
/// Exactly three scripts touch its definition - <c>02.00.00</c> creates it together with its primary
/// key, unique constraint and foreign key, <c>02.02.00</c> adds <c>HelpUrl</c> and widens
/// <c>ControlKey</c>, and <c>04.05.00</c> adds <c>SupportsPartialRendering</c> - and no later script
/// alters or drops it, so the ten columns mapped here are settled.
/// </para>
/// <para>
/// The presentation technology these entry points addressed is excluded from the migration, but the
/// rows are not: the module administration screens list the available control keys, so the entity is
/// mapped and read. <c>ControlSrc</c> carries a legacy control path through verbatim, because it
/// participates in the unique constraint and normalising it would change which rows collide.
/// </para>
/// <para>
/// The legacy schema is immutable for this migration. Nothing here creates, alters or seeds anything:
/// the table, every column, the key, the unique constraint and the cascading foreign key all already
/// exist, and this type only describes them so the model addresses them correctly.
/// </para>
/// </remarks>
internal sealed class ModuleControlConfiguration : IEntityTypeConfiguration<ModuleControl>
{
    /// <summary>
    /// Applies the terminal-schema mapping for <see cref="ModuleControl"/>.
    /// </summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations. This type declares no constructor on purpose: the scan accepts only types
    /// exposing a public parameterless constructor, so declaring a non-public one would make this
    /// configuration undiscoverable with no diagnostic of any kind, and the conventions that then
    /// took over would query a <c>ModuleDefinitionId</c> column this table does not have.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<ModuleControl> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy module model was one flattened object. ModuleInfo.vb presented
        //   roughly fifty-eight properties over the join of Modules, TabModules, ModuleDefinitions,
        //   DesktopModules and ModuleControls, so a caller could not tell which physical row a value
        //   belonged to and every read paid for the whole join - it carried this table's
        //   ModuleControlId, ControlSrc, ControlType, ControlTitle, IconFile and
        //   SupportsPartialRendering (fields at lines 86-91, properties from line 491) beside module
        //   and placement state. The target splits that object along the physical table boundaries,
        //   and this configuration maps only the ModuleControls columns.
        builder.ToTable("ModuleControls", "dbo");

        // 02.00.00:L5009-5013 - CONSTRAINT PK_ModuleControls PRIMARY KEY CLUSTERED
        // (ModuleControlID), the name at line 5010. Named explicitly so the model carries the
        // constraint name the database already has rather than one invented by convention.
        builder.HasKey(c => c.ModuleControlId)
            .HasName("PK_ModuleControls");

        // 02.00.00:L4997 - ModuleControlID int IDENTITY (1, 1) NOT NULL. The seed is recorded so a
        // model-generated script would continue the existing sequence rather than restart it. A seed
        // of 1 also means no valid key here can collide with the legacy marker for an absent integer,
        // unlike the in-scope tables whose identities seed at -1 or 0.
        builder.Property(c => c.ModuleControlId)
            .HasColumnName("ModuleControlID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: absence is expressed with nullable CLR types, never with the legacy sentinels
        //   held in Library/Components/Shared/Null.vb (-1 for an integer, the empty string for text).
        //   Legacy reads funnelled every column through Null.SetNull, so a null ModuleDefID reached
        //   callers as -1 and a null ControlKey as the empty string, leaving absence and data
        //   indistinguishable once a row had been read. The seven nullable columns below - ModuleDefID,
        //   ControlKey, ControlTitle, ControlSrc, IconFile, ViewOrder and HelpUrl - are mapped to
        //   nullable CLR properties instead, and no value converter reinstates a sentinel. The
        //   distinction is load-bearing rather than cosmetic: 02.02.00:L543-544 pairs an "is null" arm
        //   with each equality test on ControlKey and ModuleDefId, and 04.05.00:L1491 identifies the
        //   default control of a definition with "MC.ControlKey IS NULL", which the empty string would
        //   not satisfy. Where a sentinel remains observable in a published contract it is restated at
        //   the boundary layer; it is never reintroduced in persistence.

        // 02.00.00:L4998 - ModuleDefID int NULL. Nullable, and deliberately not marked required: a
        // control need belong to no definition, and 02.02.00:L544 compares this column with an
        // explicit "is null" arm precisely because the database can hold no value there. Required-ness
        // is not copied across tables - ModuleDefinitions.ModuleDefID is a key and Permission's
        // ModuleDefID is NOT NULL, yet this one is optional.
        builder.Property(c => c.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        // MIGRATION: the terminal width of ControlKey is fifty, not the twenty it was created with.
        //   02.00.00:L4999 declares ControlKey nvarchar (20) NULL and 02.02.00:L459-460 then widens it
        //   with "ALTER TABLE ModuleControls ALTER COLUMN ControlKey [nvarchar] (50)", which the same
        //   script's UpdateModuleControl procedure restates as its parameter width at line 467. Fifty
        //   is therefore the width to map: the as-created twenty would reject a legal key, and because
        //   this column participates in the unique constraint declared further below, getting it wrong
        //   would misrepresent the constraint as well as the column.
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

        // MIGRATION: ControlType is mapped as a raw int with no value conversion. The column stores
        //   the ordinal of the legacy access-level enumeration declared at
        //   Library/Components/Security/PortalSecurity.vb line 45, and that enumeration belongs to an
        //   excluded tree: it is none of the nine enumerations this migration defines, and it is
        //   deliberately not recreated here or anywhere. Three measurements say the ordinal must travel
        //   untranslated. The data boundary already treats it as an integer, since
        //   Library/Components/Providers/Data/DataProvider.vb declares "ControlType As Integer" on both
        //   AddModuleControl (line 181) and UpdateModuleControl (line 182). The persisted ordinals are
        //   not a zero-based range, because that enumeration numbers three of its members -3, -2 and
        //   -1. And the terminal SQL filters on one of those negative values directly, at
        //   02.02.00:L545, so a mapping that could not carry -2 would silently change which rows that
        //   predicate describes. No value here is reserved, checked or reinterpreted.

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

        // 02.02.00:L454-455 - ALTER TABLE ModuleControls ADD HelpUrl [nvarchar] (200) NULL. Absent
        // when the table was created. The SQL identifier is itself spelled HelpUrl, so the property
        // name and the column name agree and this binding is an identity mapping.
        builder.Property(c => c.HelpUrl)
            .HasColumnName("HelpUrl")
            .HasMaxLength(200);

        // 04.05.00:L1258-1260 - ADD SupportsPartialRendering bit NOT NULL CONSTRAINT
        // DF_ModuleControls_SupportsPartialRendering DEFAULT 0, guarded by a COLUMNPROPERTY test so
        // the upgrade is repeatable. Likewise absent at creation. The default is reproduced so an
        // insert omitting the column still lands on false rather than failing, and false here is an
        // ordinary Boolean default and not a domain sentinel - true is genuine data, which
        // 04.05.00:L1832 sets for a shipped control immediately after adding the column.
        builder.Property(c => c.SupportsPartialRendering)
            .HasColumnName("SupportsPartialRendering")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // 02.00.00:L5025-5031 - CONSTRAINT FK_ModuleControls_ModuleDefinitions FOREIGN KEY
        // (ModuleDefID) REFERENCES ModuleDefinitions (ModuleDefID) ON DELETE CASCADE NOT FOR
        // REPLICATION. The cascade is declared by the database, so it is reproduced rather than
        // softened, and it is not invented anywhere the database does not state one. The relationship
        // is optional at the same time, which the nullable key property above already expresses, so it
        // is not forced to be required.
        //
        // This configuration is the only one that declares this relationship. Assembly scanning gives
        // no ordering guarantee, so a relationship described from both ends would resolve to whichever
        // delete behaviour happened to run last, with no diagnostic at all; the convention across this
        // folder is therefore that the dependent owns the declaration, and ModuleDefinitionConfiguration
        // deliberately leaves its side of it undeclared.
        //
        // NOT FOR REPLICATION has no counterpart in the model and is deliberately unrepresented.
        builder.HasOne(c => c.ModuleDefinition)
            .WithMany(d => d.ModuleControls)
            .HasForeignKey(c => c.ModuleDefinitionId)
            .HasConstraintName("FK_ModuleControls_ModuleDefinitions")
            .OnDelete(DeleteBehavior.Cascade);

        // 02.00.00:L5016-5022 - CONSTRAINT IX_ModuleControls UNIQUE NONCLUSTERED (ModuleDefID,
        // ControlKey, ControlSrc), the name at line 5017. Never dropped by any upgrade script. The
        // column order is reproduced exactly as declared, because it decides which prefix of the key
        // can be seeked. One definition may therefore publish the same key from a different source,
        // and the same source under a different key, but not the same pair twice.
        //
        // All three columns are nullable, and the database treats a null as a comparable value here,
        // admitting at most one fully null combination, whereas the model simply declares the
        // constraint. That difference is inherent to describing a constraint the database already
        // enforces, and no filtered index or other workaround is introduced to paper over it.
        builder.HasIndex(c => new { c.ModuleDefinitionId, c.ControlKey, c.ControlSrc })
            .IsUnique()
            .HasDatabaseName("IX_ModuleControls");
    }
}
