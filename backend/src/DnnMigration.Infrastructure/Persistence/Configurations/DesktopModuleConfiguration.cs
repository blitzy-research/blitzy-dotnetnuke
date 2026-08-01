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
/// <b>The schema is immutable.</b> Nothing here creates, alters or drops a database object. This
/// type describes a table that already exists so that queries and saves resolve against it, and it
/// seeds no data. Every column is bound by an explicit name and every index by an explicit database
/// name, so renaming a member of the target model can never silently rename anything in the
/// database.
/// </para>
/// <para>
/// <b>The mapping targets the terminal schema.</b> The authoritative definition of this table is the
/// cumulative result of replaying all eighty-eight upgrade scripts under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> in version order. That history is
/// destructive as well as additive, which makes the baseline script on its own actively misleading:
/// it declares six of the thirteen columns and one index that a later script deletes outright. A
/// sweep of every script for statements touching this table finds exactly six that do -
/// <c>02.00.00</c>, <c>02.02.02</c>, <c>03.01.00</c>, <c>03.01.01</c>, <c>04.03.06</c> and
/// <c>04.05.00</c> - and nothing after <c>04.05.00</c> changes it. Each statement below cites the
/// script and line that produced the declaration it honours, so any future re-measurement has a
/// fixed starting point.
/// </para>
/// <para>
/// <b>Naming.</b> The legacy provider is registered with an empty object qualifier and <c>dbo</c> as
/// its database owner (<c>Website/release.config</c> lines 354 and 355), so the templated names in
/// the scripts resolve to unqualified names in the <c>dbo</c> schema: the table is
/// <c>DesktopModules</c>, its key constraint is <c>PK_DesktopModules</c>, and its two indexes are
/// <c>IX_DesktopModules_ModuleName</c> and <c>IX_DesktopModules_FriendlyName</c>. An installation
/// carrying a different qualifier is a configuration difference rather than a code difference, and
/// belongs where the context is composed, not here.
/// </para>
/// <para>
/// <b>Thirteen columns, corroborated twice.</b> The count agrees with the thirteen private fields of
/// the legacy class (<c>Library/Components/Modules/DesktopModuleInfo.vb</c> lines 40 to 52) and,
/// independently, with the terminal <c>AddDesktopModule</c> procedure (<c>04.05.00</c> lines 983 to
/// 996), whose twelve parameters are exactly these columns less the generated key. Anything absent
/// from that set is absent from this mapping - in particular the joined display values that the
/// legacy queries returned alongside the row, such as the portal name yielded by
/// <c>GetDesktopModulesByPortal</c>, which is a projection of another table and not a column of
/// this one.
/// </para>
/// <para>
/// <b>This type declares no relationship, deliberately.</b> Three foreign keys name this table -
/// from <c>ModuleDefinitions</c> (<c>02.00.00</c> line 5259), from <c>PortalDesktopModules</c>
/// (<c>02.02.02</c> line 3054) and from <c>PortalModuleDefinitions</c> (<c>02.02.02</c> line 3086) -
/// and every one of them is declared on the other table. <c>DesktopModules</c> carries no key column
/// of its own, so both navigations on the entity are inverse ends and each relationship is declared
/// exactly once, in the configuration of the entity that owns the key column:
/// <c>ModuleDefinitionConfiguration</c> and <c>PortalDesktopModuleConfiguration</c> respectively.
/// <c>PortalModuleDefinitions</c> is not a mapped entity, so its key needs no counterpart at all.
/// Declaring one relationship from both ends is neither a compile error nor a model-validation
/// error, and that is precisely the danger: the assembly scan that applies these configurations
/// guarantees no ordering, so whichever end ran last would silently decide the delete behaviour and
/// the cascade recorded in the scripts could be softened by accident. Declaring each relationship
/// once, on the side that owns the key, removes that hazard by construction.
/// </para>
/// </remarks>
internal sealed class DesktopModuleConfiguration : IEntityTypeConfiguration<DesktopModule>
{
    /// <summary>
    /// Applies the terminal-schema mapping for <see cref="DesktopModule"/>.
    /// </summary>
    /// <param name="builder">
    /// The builder used to configure the entity type. It is supplied by the model builder when the
    /// containing assembly is scanned for configurations, so this type declares no constructor: the
    /// scan accepts only types exposing a public parameterless constructor, and a class with no
    /// declared constructor receives exactly that from the compiler. Declaring a non-public one
    /// would make this configuration undiscoverable with no diagnostic of any kind.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<DesktopModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy module model was one flattened object.
        //   Library/Components/Modules/ModuleInfo.vb presented roughly fifty-eight properties over
        //   the join of Modules, TabModules, ModuleDefinitions, DesktopModules and ModuleControls,
        //   so a caller could not tell which physical row any given value belonged to and every read
        //   paid for the whole join. The target splits that object along the physical table
        //   boundaries - one entity per table - and this configuration maps only the columns that
        //   belong to DesktopModules. The same note appears in ModuleConfiguration,
        //   ModuleDefinitionConfiguration, ModuleControlConfiguration and TabModuleConfiguration.
        builder.ToTable("DesktopModules", "dbo");

        // 02.00.00:L5151-5154 - PK_{objectQualifier}DesktopModules PRIMARY KEY CLUSTERED
        // (DesktopModuleID). The constraint name is stated so the model names the constraint the
        // database already carries rather than inventing one by convention.
        builder.HasKey(d => d.DesktopModuleId)
            .HasName("PK_DesktopModules");

        // MIGRATION: absence is expressed with nullable CLR types, never with the legacy sentinel
        //   values defined in Library/Components/Shared/Null.vb (-1 for an integer, 255 for a byte,
        //   MinValue for the floating-point, decimal and date types, and the empty string for text).
        //   Legacy reads funnelled every column through those sentinels, so a database null and an
        //   empty string became indistinguishable the moment a row was read. The six nullable
        //   columns below therefore map to nullable properties, and no value converter reinstates a
        //   sentinel here: sentinel semantics survive only at the API boundary, where a wire
        //   contract may be externally observable. This table happens to need no sentinel care of
        //   its own, because its identity seeds at 1 (02.00.00:L5141) - unlike Portals at -1 and
        //   Roles, Tabs and Modules at 0, no valid key value on this table collides with the legacy
        //   marker for "no value".

        // 02.00.00:L5141 - [DesktopModuleID] [int] IDENTITY (1, 1) NOT NULL. The seed is recorded
        // as well as the fact of generation, so a model-generated script would reproduce the
        // existing sequence rather than restarting it.
        builder.Property(d => d.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // 02.00.00:L5142 - [FriendlyName] [nvarchar] (128) NOT NULL. Not unique in the terminal
        // schema; see the index block at the end of this method.
        builder.Property(d => d.FriendlyName)
            .HasColumnName("FriendlyName")
            .HasMaxLength(128)
            .IsRequired();

        // 02.00.00:L5143 - [Description] [nvarchar] (2000) NULL.
        builder.Property(d => d.Description)
            .HasColumnName("Description")
            .HasMaxLength(2000);

        // 02.00.00:L5144 - [Version] [nvarchar] (8) NULL. Held as text because the column is eight
        // characters wide and the scripts write and compare it as the zero-padded legacy form, for
        // example the Version = '03.01.00' assignments from 03.01.00:L43 onwards.
        builder.Property(d => d.Version)
            .HasColumnName("Version")
            .HasMaxLength(8);

        // 02.00.00:L5145 - [IsPremium] [bit] NOT NULL. The terminal schema attaches no default
        // constraint to this column, so none is declared: inventing one would make an insert that
        // omits the value succeed here and fail against the real database.
        builder.Property(d => d.IsPremium)
            .HasColumnName("IsPremium")
            .HasColumnType("bit")
            .IsRequired();

        // 02.00.00:L5146 - [IsAdmin] [bit] NOT NULL. No default constraint, for the same reason.
        builder.Property(d => d.IsAdmin)
            .HasColumnName("IsAdmin")
            .HasColumnType("bit")
            .IsRequired();

        // 02.02.02:L116-117 - ALTER TABLE ... ADD BusinessControllerClass nvarchar (200). The script
        // states no nullability, so the column takes the permissive default and the property is
        // nullable to match.
        builder.Property(d => d.BusinessControllerClass)
            .HasColumnName("BusinessControllerClass")
            .HasMaxLength(200);

        // 03.01.00:L12 adds [FolderName] nvarchar(128) NULL, L18 back-fills it from FriendlyName and
        // L22-23 promotes it with ALTER COLUMN ... NOT NULL. The terminal state is what is mapped,
        // so this is required rather than optional; mapping the historical add would let a null
        // reach a column that rejects it.
        builder.Property(d => d.FolderName)
            .HasColumnName("FolderName")
            .HasMaxLength(128)
            .IsRequired();

        // 03.01.00:L13 adds [ModuleName] nvarchar(128) NULL, L19 back-fills it from FriendlyName and
        // L26-27 promotes it with ALTER COLUMN ... NOT NULL. Required for the same reason, and the
        // one column the terminal schema constrains to be unique.
        builder.Property(d => d.ModuleName)
            .HasColumnName("ModuleName")
            .HasMaxLength(128)
            .IsRequired();

        // 03.01.00:L14 adds [SupportedFeatures] int NOT NULL with the default constraint
        // DF_{objectQualifier}DesktopModules_SupportedFeatures DEFAULT 0; 03.01.01:L912 re-asserts
        // NOT NULL and L914 re-creates that default after dropping it by name. The default is
        // reproduced so an insert that omits the column lands on "no optional capability" exactly as
        // the database would decide, rather than failing.
        builder.Property(d => d.SupportedFeatures)
            .HasColumnName("SupportedFeatures")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // 04.03.06:L53-55 - guarded ALTER TABLE ... ADD CompatibleVersions nvarchar(500) NULL.
        builder.Property(d => d.CompatibleVersions)
            .HasColumnName("CompatibleVersions")
            .HasMaxLength(500);

        // 04.05.00:L965-967 - guarded ALTER TABLE ... ADD Dependencies nvarchar(400) NULL.
        builder.Property(d => d.Dependencies)
            .HasColumnName("Dependencies")
            .HasMaxLength(400);

        // 04.05.00:L970-972 - guarded ALTER TABLE ... ADD Permissions nvarchar(400) NULL. Despite
        // the name this is installer metadata declared by the package concerning itself, unrelated
        // to the portal permission model mapped by PermissionConfiguration.
        builder.Property(d => d.Permissions)
            .HasColumnName("Permissions")
            .HasMaxLength(400);

        // MIGRATION: the three capability properties are computed, not stored, and must be excluded
        //   explicitly. On the legacy class they were read/write facades over a single integer bit
        //   field - IsUpgradeable at DesktopModuleInfo.vb:L155-162, IsPortable at L164-171 and
        //   IsSearchable at L173-180 - each delegating to the guarded mask test at L220-229 against
        //   the masks 4, 1 and 2 declared by the nested capability enumeration at L30-34. That
        //   enumeration is excluded from scope and is deliberately not recreated as a type, so
        //   SupportedFeatures is mapped above as a plain integer with no value converter and no
        //   flag type: the bit field is persistence data, and publishing it as vocabulary would
        //   invite callers to store or transport a mask.
        //
        //   None of the three is a column. Sweeping all eighty-eight scripts finds no IsPortable,
        //   IsSearchable or IsUpgradeable column on this table at any point in its history, so
        //   leaving them to convention would have the provider ask for columns the database does not
        //   have and every query against DesktopModules would fail at run time with an invalid
        //   column name. The exclusion belongs here rather than as an attribute on the entity,
        //   because the Domain project references no package and must stay ignorant of persistence.
        //
        //   The inherited Identity member needs no such call. It is a get-only, expression-bodied
        //   override with neither a setter nor a backing field, so the property-discovery convention
        //   never treats it as a candidate; the key is named explicitly above from the real column
        //   instead. The three calls below are the only exclusions this table requires.
        builder.Ignore(d => d.IsPortable);
        builder.Ignore(d => d.IsSearchable);
        builder.Ignore(d => d.IsUpgradeable);

        // 03.01.00:L30-31 - ALTER TABLE ... ADD CONSTRAINT
        // IX_{objectQualifier}DesktopModules_ModuleName UNIQUE NONCLUSTERED (ModuleName). This is
        // the only uniqueness the terminal schema imposes on the table, and it is what makes
        // ModuleName the natural key that the legacy GetDesktopModuleByModuleName query relies on.
        builder.HasIndex(d => d.ModuleName)
            .IsUnique()
            .HasDatabaseName("IX_DesktopModules_ModuleName");

        // MIGRATION: measured correction to the plan text, which asserted a unique index on
        //   FriendlyName as well as on ModuleName. The terminal schema has no such constraint, and
        //   the lifecycle is unambiguous once the templated constraint names are normalised:
        //     02.00.00:L5158-5161  ADD CONSTRAINT IX_{objectQualifier}DesktopModules
        //                          UNIQUE NONCLUSTERED (FriendlyName)
        //     03.01.00:L30-31      ADD CONSTRAINT IX_{objectQualifier}DesktopModules_ModuleName
        //                          UNIQUE NONCLUSTERED (ModuleName)
        //     03.01.00:L34-35      DROP CONSTRAINT IX_{objectQualifier}DesktopModules
        //     03.01.00:L38         CREATE NONCLUSTERED INDEX
        //                          IX_{objectQualifier}DesktopModules_FriendlyName (FriendlyName)
        //   So the original uniqueness on FriendlyName is dropped and replaced by a plain index, and
        //   the dropped constraint's name is never reproduced. The index below is therefore not
        //   marked unique. Asserting uniqueness it does not have would be worse than cosmetic: the
        //   change tracker would reject duplicate display names the database accepts, and any script
        //   generated from this model would try to add a constraint the data may already violate -
        //   plausibly so, since 03.01.00:L18-19 back-fills both FolderName and ModuleName from
        //   FriendlyName, leaving upgraded installations full of repeated display names. This
        //   correction is reported rather than quietly absorbed. Note that the sibling
        //   ModuleDefinitions table does retain a unique index on its own FriendlyName column; the
        //   two must not be conflated, and that one is configured elsewhere.
        builder.HasIndex(d => d.FriendlyName)
            .HasDatabaseName("IX_DesktopModules_FriendlyName");
    }
}
