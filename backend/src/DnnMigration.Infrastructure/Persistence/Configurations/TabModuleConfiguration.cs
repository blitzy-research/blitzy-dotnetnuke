using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="TabModule"/> entity to the existing <c>dbo.TabModules</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This table is the <i>placement</i> of a module on a page, and it exists because the 03.00.01
/// upgrade took the placement columns off <c>dbo.Modules</c> and re-homed them here. Everything about
/// how a module appears - its pane, its order within that pane, its cache window, its alignment,
/// colour, border, icon, visibility state and container - is a fact of the placement rather than of
/// the module, which is precisely what the legacy flattened view of the two obscured.
/// </para>
/// <para>
/// The schema is immutable for this migration. Every declaration below describes the terminal state
/// of the 87-script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> as it already stands; none of it creates,
/// alters or drops anything, and the baseline migration is intentionally empty. Table and column
/// names are bound explicitly rather than left to convention, because the legacy spellings
/// (<c>TabModuleID</c>, <c>TabID</c>, <c>ModuleID</c>) are not the ones convention would produce.
/// The legacy provider registration supplies the naming defaults: an empty object qualifier at
/// <c>Website/release.config</c> line 354 and <c>dbo</c> as the database owner at line 355.
/// </para>
/// <para>
/// The unique index <c>IX_TabModules</c> over <c>(TabID, ModuleID)</c> is the core semantic of the
/// table: a module instance appears at most once on any given page, while still appearing on as many
/// pages as required. Both foreign keys are required and both cascade, exactly as the database
/// declares them.
/// </para>
/// <para>
/// Three details on this table are easy to get wrong and are each pinned deliberately below:
/// <c>Border</c> is <c>nvarchar(1)</c> rather than ANSI <c>char(1)</c>, so no ANSI override appears
/// anywhere in this file - every string column here is Unicode; <c>Visibility</c> stores the legacy
/// <see cref="ModuleVisibility"/> ordinals and takes no value conversion; and the three display
/// flags carry a store default of <c>1</c> that is recorded for fidelity and then pinned with
/// <c>ValueGeneratedNever</c> so that recording it cannot change what gets written.
/// </para>
/// </remarks>
internal sealed class TabModuleConfiguration : IEntityTypeConfiguration<TabModule>
{
    /// <summary>
    /// Applies the mapping for the placement entity type.
    /// </summary>
    /// <param name="builder">
    /// The builder for <see cref="TabModule"/>. This type declares no constructor on purpose:
    /// <c>ApplyConfigurationsFromAssembly</c> instantiates each configuration through its public
    /// parameterless constructor, so declaring a non-public one would make this configuration
    /// undiscoverable with no diagnostic of any kind, and the conventions that then took over would
    /// look for columns this table does not have.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<TabModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy module model was one flattened object. ModuleInfo.vb presented
        //   roughly fifty-eight properties over the join of Modules, TabModules, ModuleDefinitions,
        //   DesktopModules and ModuleControls, and implemented IPropertyAccess besides
        //   (Library/Components/Modules/ModuleInfo.vb lines 36-37) to serve the token-replacement
        //   subsystem this migration excludes. A caller could not tell which physical row a value
        //   belonged to, and every read paid for the whole join. The target splits that object along
        //   the PHYSICAL TABLE BOUNDARIES, and this configuration maps only the columns of this one
        //   table - the PLACEMENT columns. Eight of them were physically moved here rather than
        //   copied: PaneName, ModuleOrder, CacheTime, Alignment, Color, Border, IconFile and
        //   ContainerSrc were dropped from dbo.Modules in a single statement at 03.00.01:L197-198,
        //   moments after 03.00.01:L19-33 created dbo.TabModules to receive them, and the page key
        //   itself went at 03.00.01:L204-205. Binding any of them on Module would fail at run time
        //   on an invalid column name. The same note appears in ModuleConfiguration,
        //   ModuleDefinitionConfiguration, ModuleControlConfiguration and DesktopModuleConfiguration.
        builder.ToTable("TabModules", "dbo");

        // 03.00.01:L36-40 - ALTER TABLE TabModules ADD CONSTRAINT PK_TabModules PRIMARY KEY
        // CLUSTERED (TabModuleID), the constraint name at line 37. Named explicitly so the model
        // carries the name the database already has rather than one invented by convention.
        // CLUSTERED is a physical storage choice with no counterpart in the model and is left
        // unrepresented, here and across this folder.
        builder.HasKey(t => t.TabModuleId).HasName("PK_TabModules");

        // int NOT NULL IDENTITY (1, 1) [03.00.01:L21]. The seed is preserved rather than left to the
        // provider default, so the model states the same starting point the database does.
        //
        // MIGRATION: this key seeds at 1, and it is the ONLY identifier in the module aggregate that
        //   does. Its two parents both seed at ZERO - dbo.Tabs.TabID is IDENTITY (0, 1)
        //   [01.00.00:L140] and dbo.Modules.ModuleID is IDENTITY (0, 1) [01.00.00:L221] - so a TabID
        //   or a ModuleID of 0 in this table is a legitimate reference to a real first row and never
        //   means "absent". That matters because the legacy sentinel system in
        //   Library/Components/Shared/Null.vb used -1 for an absent integer while the CLR uses 0 for
        //   an unassigned one, and both values collide with real keys in this schema. Nothing in
        //   this file treats either value as a marker: no sentinel is redefined on TabID or ModuleID
        //   because neither is a key property here, so Entity Framework always writes whatever value
        //   they hold. Contrast ModuleSettingConfiguration, where ModuleId is both the primary key
        //   and the foreign key and therefore does need its sentinel moved off zero.
        builder.Property(t => t.TabModuleId)
            .HasColumnName("TabModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // int NOT NULL [03.00.01:L22] - the page half of the placement, and the foreign key
        // FK_TabModules_Tabs configured below. Required, with no default constraint in the schema
        // and therefore none recorded here.
        builder.Property(t => t.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .IsRequired();

        // int NOT NULL [03.00.01:L23] - the instance half of the placement, and the foreign key
        // FK_TabModules_Modules configured below. Required, and likewise carrying no default.
        builder.Property(t => t.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(50) NOT NULL [03.00.01:L24] - the only non-nullable string on this table. The
        // width is declared so an over-long pane name is rejected here rather than truncated by the
        // provider.
        builder.Property(t => t.PaneName)
            .HasColumnName("PaneName")
            .HasMaxLength(50)
            .IsRequired();

        // int NOT NULL [03.00.01:L25] - the ordinal within the pane, not within the page.
        builder.Property(t => t.ModuleOrder)
            .HasColumnName("ModuleOrder")
            .HasColumnType("int")
            .IsRequired();

        // int NOT NULL [03.00.01:L26] - the output cache window in minutes, zero meaning uncached.
        // Zero is a real value here rather than an absence, so the column stays non-nullable and
        // takes no default.
        builder.Property(t => t.CacheTime)
            .HasColumnName("CacheTime")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(10) NULL [03.00.01:L27]. Nullable, so deliberately no IsRequired: a database null
        // materialises as a CLR null and means the placement expresses no preference. It is not the
        // empty string, which is what the legacy sentinel Null.NullString actually was.
        builder.Property(t => t.Alignment)
            .HasColumnName("Alignment")
            .HasMaxLength(10);

        // nvarchar(20) NULL [03.00.01:L28]. An opaque legacy colour token carried through unchanged
        // rather than parsed, because the store admits anything twenty characters or shorter and this
        // migration does not narrow an existing contract.
        builder.Property(t => t.Color)
            .HasColumnName("Color")
            .HasMaxLength(20);

        // MIGRATION: nvarchar(1) NULL [03.00.01:L29] - a UNICODE column one character wide, not ANSI
        //   char(1). The width is declared verbatim rather than rounded up, and no ANSI override is
        //   applied here or anywhere else in this file, because this table has no non-Unicode column
        //   at all. The lookalike to keep it apart from is dbo.Roles.BillingFrequency, which
        //   genuinely is char(1) and is mapped as such in RoleConfiguration; applying that treatment
        //   here would change this column's collation and storage on an existing table.
        builder.Property(t => t.Border)
            .HasColumnName("Border")
            .HasMaxLength(1);

        // nvarchar(100) NULL [03.00.01:L30] - a portal-relative icon path for this placement alone.
        builder.Property(t => t.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // MIGRATION: Visibility stores the legacy ModuleVisibility ORDINALS - Maximized as 0,
        //   Minimized as 1, None as 2 - directly in the existing int NOT NULL column
        //   [03.00.01:L31], and NO value conversion is applied. Entity Framework's default
        //   enum-to-int mapping already persists exactly those numbers, which are load-bearing legacy
        //   data: converting to a string, or renumbering the enumeration, would re-point every
        //   existing row at a different meaning. The two lookalikes elsewhere in this folder do take
        //   a conversion, and neither pattern belongs here: Permission.PermissionKey converts to a
        //   string and Role.BillingFrequency to a char, because those columns really do hold text.
        //   This column belongs to dbo.TabModules and to nothing else - dbo.Modules has no
        //   Visibility column, which is why ModuleConfiguration correctly maps none.
        //
        // The builder is typed explicitly as PropertyBuilder<ModuleVisibility> so the ordinal
        // contract is checked by the compiler rather than trusted: were the domain property ever
        // widened to int or retyped, this declaration stops compiling instead of silently changing
        // how existing rows are read.
        PropertyBuilder<ModuleVisibility> visibility = builder.Property(t => t.Visibility);

        visibility
            .HasColumnName("Visibility")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(200) NULL [03.00.01:L32] - the container skin drawn around the placement. Null
        // means it inherits whatever the page or portal specifies.
        builder.Property(t => t.ContainerSrc)
            .HasColumnName("ContainerSrc")
            .HasMaxLength(200);

        // All three display flags arrived together at 03.00.08:L155-158, each declared
        // "bit NOT NULL CONSTRAINT DF_{objectQualifier}TabModules_<column> DEFAULT (1)", and all
        // three survive into the terminal schema: 03.01.01 drops the three default constraints
        // through dynamic SQL (L1185-1193, L1195-1203 and L1205-1213), re-declares the columns
        // bit NOT NULL at L1215, L1216 and L1217, and re-adds DEFAULT (1) at L1219, L1221 and L1223.
        // No later script touches this table at all.
        //
        // Each therefore records its store default for schema fidelity and then pins the column with
        // ValueGeneratedNever, which is the convention this folder already applies to Tabs.IsVisible
        // and UserPortals.Authorised. Recording a default is otherwise unsafe: it sets
        // ValueGenerated.OnAdd, and Entity Framework then OMITS the column from an insert whenever
        // the value equals the property sentinel - which for bool is false. Unpinned, a caller asking
        // to hide a title, a print affordance or a syndication affordance would have the request
        // quietly reversed into the store default of true. Pinned, the default stays described and
        // false is written as false. Recording the default alters nothing in the database; the
        // constraints above already exist.
        builder.Property(t => t.DisplayTitle)
            .HasColumnName("DisplayTitle")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        builder.Property(t => t.DisplayPrint)
            .HasColumnName("DisplayPrint")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // MIGRATION: DELIBERATE, DOCUMENTED DIVERGENCE, and the one member of the three flags where
        //   the legacy object default and the database default genuinely disagree. Do not "tidy"
        //   either side into agreement with the other.
        //     * the store says 1, that is TRUE, and it is not ambiguous - three independent
        //       witnesses: the original add at 03.00.08:L158, the re-declaration at 03.01.01:L1217
        //       and the re-added constraint DF_{objectQualifier}TabModules_DisplaySyndicate
        //       DEFAULT (1) at 03.01.01:L1223.
        //     * object construction says FALSE. Library/Components/Modules/ModuleInfo.vb sets
        //       _DisplaySyndicate = False in the constructor at line 124 and again in the separate
        //       Initialize routine at line 746, and the target entity reproduces that with
        //       "DisplaySyndicate { get; set; } = false" while DisplayTitle and DisplayPrint both
        //       initialise to true.
        //   THE DATABASE DEFAULT IS PRESERVED UNCHANGED. Rule T4 makes the schema immutable, so the
        //   store default is recorded here as true exactly as on the other two flags and is not
        //   rewritten to match the CLR initialiser; equally the domain entity is not edited to match
        //   the store, because Domain files are owned elsewhere and because either edit would change
        //   observable behaviour on one side. ValueGeneratedNever is what lets both survive: a
        //   placement created through this model persists false, matching every placement the legacy
        //   application created, while a row inserted by anything that omits the column - a stored
        //   procedure, a script, a hand-written statement - still gets 1, matching the legacy
        //   database. Callers wanting the store's answer must assign it explicitly. Recorded in the
        //   repository-root MIGRATION_NOTES.md rather than silently absorbed.
        builder.Property(t => t.DisplaySyndicate)
            .HasColumnName("DisplaySyndicate")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // ALTER TABLE TabModules ADD CONSTRAINT IX_TabModules UNIQUE NONCLUSTERED (TabID, ModuleID)
        // [03.00.03:L11-16], never dropped outside UnInstall.SqlDataProvider. The column order is
        // part of the definition and is reproduced as declared: TabID first, then ModuleID. IsUnique
        // is the whole point of the constraint - it is what stops the same instance being placed
        // twice on one page - so it is not optional here. The physical name is preserved rather than
        // left to convention. This is the only index on the table besides the clustered primary key;
        // Entity Framework adds its own covering index for the ModuleID foreign key by convention,
        // and the TabID foreign key needs none because it is the leading column of this one.
        builder.HasIndex(t => new { t.TabId, t.ModuleId })
            .IsUnique()
            .HasDatabaseName("IX_TabModules");

        // MIGRATION: both foreign keys are declared ON DELETE CASCADE in the schema and both are
        //   reproduced faithfully - FK_TabModules_Tabs on TabID referencing dbo.Tabs at
        //   03.00.01:L44-52 and FK_TabModules_Modules on ModuleID referencing dbo.Modules at
        //   03.00.01:L56-64. Deleting a page or a module instance removes its placements in the
        //   database, and that behaviour must not be softened to NoAction or Restrict to avoid the
        //   familiar "multiple cascade paths" complaint: that diagnostic comes from the MIGRATION
        //   GENERATOR, not from the model validator, and the baseline migration in this solution is
        //   intentionally empty under Rule T4. Downgrading either edge would make the model describe
        //   a database that does not exist and would leave orphan placements behind on any delete
        //   the model performed itself.
        //
        //   NOT FOR REPLICATION appears on both constraints and has no Entity Framework counterpart;
        //   it is deliberately unrepresented, here and across this folder.
        //
        // Both relationships are configured from THIS side only, because TabModule is the dependent
        // of both: it holds TabID and ModuleID. TabConfiguration and ModuleConfiguration deliberately
        // declare nothing for these two edges. Configuring a relationship from both ends is silently
        // destructive - ApplyConfigurationsFromAssembly gives no ordering guarantee, so whichever
        // delete behaviour ran last would win, with no compile error and no model-validation error.
        // Neither call states IsRequired: the non-nullable int foreign keys already make both
        // relationships required, exactly as the NOT NULL columns do.
        builder.HasOne(t => t.Tab)
            .WithMany(tab => tab.TabModules)
            .HasForeignKey(t => t.TabId)
            .HasConstraintName("FK_TabModules_Tabs")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Module)
            .WithMany(m => m.TabModules)
            .HasForeignKey(t => t.ModuleId)
            .HasConstraintName("FK_TabModules_Modules")
            .OnDelete(DeleteBehavior.Cascade);

        // The TabModuleSetting collection on this entity is the INVERSE side of a relationship the
        // dependent owns, so it is configured by TabModuleSettingConfiguration and not here. No
        // HasMany belongs in this file, and Entity<int>.Identity needs no Ignore: it is a get-only
        // expression-bodied override with no setter and no backing field, which Entity Framework
        // already excludes because it cannot be written.
    }
}
