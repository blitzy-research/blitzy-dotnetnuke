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
/// <b>The schema is immutable.</b> Nothing here creates, alters, drops or seeds a database object.
/// Every column is bound by an explicit name and every index by an explicit database name, so
/// renaming a member of the model can never silently rename anything in the database.
/// </para>
/// <para>
/// <b>The mapping targets the terminal schema</b> - the cumulative result of replaying all
/// eighty-eight upgrade scripts under <c>Website/Providers/DataProviders/SqlDataProvider/</c> in
/// version order. That history is destructive as well as additive, so the baseline script alone is
/// actively misleading: it declares six of the thirteen columns and one index that a later script
/// deletes outright. Six scripts touch this table - <c>02.00.00</c>, <c>02.02.02</c>,
/// <c>03.01.00</c>, <c>03.01.01</c>, <c>04.03.06</c> and <c>04.05.00</c> - and nothing after
/// <c>04.05.00</c> changes it, which is why each statement below cites the script and line it
/// honours. The thirteen columns are corroborated independently by the thirteen private fields of
/// <c>Library/Components/Modules/DesktopModuleInfo.vb</c> and by the twelve parameters of the
/// terminal <c>AddDesktopModule</c> procedure, which are these columns less the generated key.
/// Values the legacy queries returned alongside the row but that belong to another table are
/// projections and are not mapped here. The legacy provider registration uses an empty object
/// qualifier and <c>dbo</c> as the database owner, so the templated script names resolve
/// unqualified in <c>dbo</c>; an installation carrying a different qualifier is a configuration
/// difference, resolved where the context is composed rather than here.
/// </para>
/// <para>
/// <b>No relationship is declared here, deliberately.</b> Three foreign keys name this table, and
/// every one of them lives on the other table; <c>DesktopModules</c> carries no key column of its
/// own, so both navigations on the entity are inverse ends. Each relationship must be declared
/// exactly once, by the configuration of the entity that owns the key column. Declaring one from
/// both ends is neither a compile error nor a model-validation error, and that is precisely the
/// hazard: the assembly scan that applies these configurations guarantees no ordering, so whichever
/// end ran last would silently decide the delete behaviour and could soften the cascade the scripts
/// record.
/// </para>
/// </remarks>
internal sealed class DesktopModuleConfiguration : IEntityTypeConfiguration<DesktopModule>
{
    /// <summary>
    /// Applies the terminal-schema mapping for <see cref="DesktopModule"/>.
    /// </summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations. This type declares no constructor on purpose: the scan accepts only types
    /// exposing a public parameterless constructor, so declaring a non-public one would make the
    /// configuration undiscoverable with no diagnostic of any kind.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<DesktopModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy module model was one flattened object. ModuleInfo.vb presented
        //   roughly fifty-eight properties over the join of Modules, TabModules, ModuleDefinitions,
        //   DesktopModules and ModuleControls, so a caller could not tell which physical row a value
        //   belonged to and every read paid for the whole join. The target splits that object along
        //   the physical table boundaries, and this configuration maps only DesktopModules columns.
        builder.ToTable("DesktopModules", "dbo");

        // 02.00.00:L5151-5154 - PK_{objectQualifier}DesktopModules PRIMARY KEY CLUSTERED
        // (DesktopModuleID). Named explicitly so the model carries the constraint name the database
        // already has rather than one invented by convention.
        builder.HasKey(d => d.DesktopModuleId)
            .HasName("PK_DesktopModules");

        // MIGRATION: absence is expressed with nullable CLR types, never with the legacy sentinels
        //   (-1 for an integer, 255 for a byte, MinValue for the floating-point, decimal and date
        //   types, and the empty string for text). Legacy reads funnelled every column through them,
        //   so a database null and an empty string became indistinguishable once a row was read. No
        //   value converter reinstates a sentinel here; sentinel semantics survive only at the API
        //   boundary, where a wire contract may be externally observable. This table needs no
        //   sentinel care of its own because its identity seeds at 1 - unlike Portals at -1 and
        //   Roles, Tabs and Modules at 0, no valid key value here collides with the legacy marker.

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
        // attaches no default constraint to either, so none is declared: inventing one would let an
        // insert that omits the value succeed here and fail against the real database.
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

        // 03.01.00: L12 adds [FolderName] nvarchar(128) NULL, L18 back-fills it from FriendlyName
        // and L22-23 promotes it to NOT NULL. The terminal state is mapped, so this is required;
        // mapping the historical add would let a null reach a column that rejects it.
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
        // re-asserts NOT NULL and re-creates that default after dropping it by name. The default is
        // reproduced so an insert that omits the column lands on "no optional capability" exactly as
        // the database would decide, rather than failing.
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

        // 04.05.00:L970-972 - guarded ALTER TABLE ... ADD Permissions nvarchar(400) NULL. Despite
        // the name this is installer metadata a package declares about itself, unrelated to the
        // portal permission model.
        builder.Property(d => d.Permissions)
            .HasColumnName("Permissions")
            .HasMaxLength(400);

        // MIGRATION: the three capability properties are computed, not stored, and must be excluded
        //   explicitly. On the legacy class each was a read/write facade over one integer bit field,
        //   testing the masks 4, 1 and 2 through a nested capability enumeration that is out of
        //   scope and deliberately not recreated - which is why SupportedFeatures is mapped above as
        //   a plain integer with no converter and no flag type. Sweeping all eighty-eight scripts
        //   finds no IsPortable, IsSearchable or IsUpgradeable column at any point in this table's
        //   history, so leaving them to convention would make every query against DesktopModules
        //   fail at run time with an invalid column name. The exclusion belongs here rather than on
        //   the entity, because the Domain project references no package and must stay ignorant of
        //   persistence. The inherited Identity member needs no such call: it is a get-only,
        //   expression-bodied override with no setter and no backing field, so property discovery
        //   never treats it as a candidate.
        builder.Ignore(d => d.IsPortable);
        builder.Ignore(d => d.IsSearchable);
        builder.Ignore(d => d.IsUpgradeable);

        // Uniqueness on this table is asymmetric and measured. 03.01.00:L30-31 adds CONSTRAINT
        // IX_{objectQualifier}DesktopModules_ModuleName UNIQUE NONCLUSTERED (ModuleName) - the only
        // uniqueness the terminal schema imposes, and what makes ModuleName the natural key the
        // legacy GetDesktopModuleByModuleName query relies on. FriendlyName once carried a unique
        // constraint (02.00.00:L5158-5161), but 03.01.00 drops it at L34-35 and replaces it at L38
        // with a plain nonclustered index, so the FriendlyName index below is deliberately not
        // unique: asserting uniqueness the database does not have would make the change tracker
        // reject duplicate display names the database accepts, and 03.01.00:L18-19 back-fills
        // FolderName and ModuleName from FriendlyName, leaving upgraded installations full of
        // repeated display names. The sibling ModuleDefinitions table does keep a unique index on
        // its own FriendlyName column; the two must not be conflated.
        builder.HasIndex(d => d.ModuleName)
            .IsUnique()
            .HasDatabaseName("IX_DesktopModules_ModuleName");

        builder.HasIndex(d => d.FriendlyName)
            .HasDatabaseName("IX_DesktopModules_FriendlyName");
    }
}
