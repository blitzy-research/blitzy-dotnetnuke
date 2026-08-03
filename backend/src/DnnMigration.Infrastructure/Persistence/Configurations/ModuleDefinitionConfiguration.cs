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
/// <b>The schema is immutable.</b> Nothing here creates, alters, drops or seeds a database object.
/// Every column is bound by an explicit name and every index by an explicit database name, so
/// renaming a member of the model can never silently rename anything in the database.
/// </para>
/// <para>
/// <b>The mapping targets the terminal schema</b> - the cumulative result of replaying all
/// eighty-eight upgrade scripts under <c>Website/Providers/DataProviders/SqlDataProvider/</c> in
/// version order. That history is destructive as well as additive, which makes the baseline script
/// actively misleading for this table in particular: it declares seven columns of which only two
/// survive. Seven scripts touch the table - <c>01.00.00</c>, <c>01.00.05</c>, <c>01.00.07</c>,
/// <c>01.00.08</c>, <c>02.00.00</c>, <c>03.01.00</c> and <c>03.01.01</c> - and nothing after
/// <c>03.01.01</c> changes it, which is why each statement below cites the script and line it
/// honours. The legacy provider registration uses an empty object qualifier and <c>dbo</c> as the
/// database owner (<c>Website/release.config</c> lines 354-355), so the templated script names
/// resolve unqualified in <c>dbo</c>; an installation carrying a different qualifier is a
/// configuration difference, resolved where the context is composed rather than here.
/// </para>
/// <para>
/// <b>Four columns, and four mapped properties - no more.</b> The count is corroborated twice over,
/// independently of the schema. <c>Library/Components/Providers/Data/DataProvider.vb</c> lines
/// 170-175 declare the whole legacy surface for this table as six members, and the two that write
/// carry three parameters each: the terminal <c>AddModuleDefinition</c> procedure
/// (<c>03.01.00</c> line 304) takes the owning package, the friendly name and the default cache
/// time, which is these columns less the generated key, and <c>UpdateModuleDefinition</c>
/// (line 327) takes the key, the friendly name and the cache time - so the owning package is fixed
/// at insertion and never rewritten.
/// </para>
/// <para>
/// <b>One relationship is declared here, and exactly one.</b> This entity is the dependent half of
/// the package-to-definition relationship because it carries the key column, so it owns that
/// relationship. Its four inverse navigations are owned by the four dependents that carry the
/// matching key columns - <c>ModuleControlConfiguration</c>, <c>ModuleConfiguration</c>,
/// <c>PermissionConfiguration</c> and <c>ProfilePropertyDefinitionConfiguration</c>. Declaring a
/// relationship from both ends is neither a compile error nor a model-validation error, and that is
/// precisely the hazard: the assembly scan that applies these configurations guarantees no
/// ordering, so whichever end ran last would silently decide the delete behaviour and could soften
/// a cascade the scripts record.
/// </para>
/// </remarks>
internal sealed class ModuleDefinitionConfiguration : IEntityTypeConfiguration<ModuleDefinition>
{
    /// <summary>
    /// Applies the terminal-schema mapping for <see cref="ModuleDefinition"/>.
    /// </summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations. This type declares no constructor on purpose: the scan accepts only types
    /// exposing a public parameterless constructor, so declaring a non-public one would make the
    /// configuration undiscoverable with no diagnostic of any kind, and the conventions that then
    /// took over would look for a <c>ModuleDefinitionId</c> column this table does not have.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<ModuleDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy module model was one flattened object. ModuleInfo.vb presented
        //   roughly fifty-eight properties over the join of Modules, TabModules, ModuleDefinitions,
        //   DesktopModules and ModuleControls, so a caller could not tell which physical row a
        //   value belonged to and every read paid for the whole join - it exposed this table's
        //   ModuleDefID at line 167, FriendlyName at line 374 and DefaultCacheTime at line 482
        //   beside DesktopModules.DesktopModuleID at line 365 and the per-placement CacheTime at
        //   line 203. The target splits that object along the physical table boundaries, and this
        //   configuration maps only ModuleDefinitions columns.
        builder.ToTable("ModuleDefinitions", "dbo");

        // 01.00.00:L462-467 - CONSTRAINT PK_ModuleDefinitions PRIMARY KEY NONCLUSTERED
        // (ModuleDefID), the name at line 464. Named explicitly so the model carries the constraint
        // name the database already has rather than one invented by convention.
        //
        // MIGRATION: NONCLUSTERED is expressed rather than left to the provider, whose default for a
        // primary key is CLUSTERED. Recording the clustering in the model is what keeps the snapshot a
        // truthful description of the existing table, so a later scaffold cannot propose rebuilding a
        // key that is already correct.
        builder.HasKey(m => m.ModuleDefinitionId)
            .HasName("PK_ModuleDefinitions")
            .IsClustered(false);

        // MIGRATION: the property is spelled ModuleDefinitionId in full, but the column keeps the
        //   abbreviated legacy spelling ModuleDefID (01.00.00:L66). That abbreviation is not local
        //   to this table - it is the spelling every table that references a definition uses, in
        //   Modules, ModuleControls, Permission and ProfilePropertyDefinition alike - so the
        //   explicit HasColumnName below is the single thing standing between the model and a
        //   query against a column that does not exist.
        //
        // 01.00.00:L66 - ModuleDefID int IDENTITY (1, 1) NOT NULL. The seed is recorded so a
        // model-generated script would continue the existing sequence rather than restart it. Unlike
        // Portals at -1 and Roles, Tabs and Modules at 0, this seed of 1 means no valid key here
        // collides with the legacy "absent integer" marker, so this table needs no sentinel care of
        // its own.
        builder.Property(m => m.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: ten columns this table once carried are absent from the terminal schema and are
        //   deliberately left unmapped; mapping any of them would fail at run time on an invalid
        //   column name. Nine went in a single statement at 02.00.00:L5254-5255 - DesktopSrc,
        //   MobileSrc, EditSrc, Secure, EditModuleIcon, AdminTabIcon, AdminOrder, Description and
        //   IsPremium - when that script split the host-level package into dbo.DesktopModules and
        //   left this table holding only what a package publishes. HostFee had already gone at
        //   01.00.08:L5891-5892. Four of the nine were relocated rather than discarded, which is why
        //   searching for them still finds live data: Description and IsPremium moved to
        //   dbo.DesktopModules and are reached through the DesktopModule navigation, AdminTabIcon
        //   moved to dbo.Tabs.IconFile and EditModuleIcon to dbo.Modules.IconFile. The remaining
        //   five have no successor column anywhere - DesktopSrc, MobileSrc and EditSrc were
        //   per-definition control paths superseded by dbo.ModuleControls rows, Secure was a
        //   page-level flag whose default constraint went at 02.00.00:L5246-5247, and AdminOrder
        //   survives only as the boolean it was converted into on DesktopModule. Three of the ten
        //   were themselves later additions, so their whole lifetime is inside this history:
        //   Description and HostFee at 01.00.05:L442-444, AdminTabIcon and EditModuleIcon at
        //   01.00.07:L169-171 and IsPremium at 01.00.08:L5874-5875.

        // 01.00.00:L67 - FriendlyName nvarchar (128) NOT NULL. Unicode is the default for a string,
        // so it is not restated; this is the only text column on the table.
        builder.Property(m => m.FriendlyName)
            .HasColumnName("FriendlyName")
            .HasMaxLength(128)
            .IsRequired();

        // 02.00.00:L5173-5174 adds DesktopModuleID int NOT NULL together with the default constraint
        // DF_ModuleDefinitions_DesktopModuleID DEFAULT 0, and L5242-5243 of that same script drops
        // that constraint again once its back-fill loop has run.
        //
        // MIGRATION: the terminal column is therefore required with NO default, and none is declared
        //   here. This is the one place where a plausible reading of the history is actively
        //   dangerous, so it is recorded rather than left to inference. Declaring HasDefaultValue(0)
        //   would make Entity Framework Core treat zero as "no value supplied" and omit the column
        //   from the generated INSERT; because the database has no default to fall back on, SQL
        //   Server would then reject the row outright. Four measurements agree that the default is
        //   gone: the drop above, the absence of any default constraint on this column in a live
        //   installation, the terminal AddModuleDefinition procedure at 03.01.00:L312-321 which
        //   inserts the value explicitly, and PaDnnInstallerBase.vb line 406 which assigns the
        //   package identity immediately before that insert. Nothing may read zero here as "no
        //   package" either: it is a real package identity or a row that could not have been
        //   inserted.
        builder.Property(m => m.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .IsRequired();

        // 03.01.00:L297-298 adds DefaultCacheTime int NOT NULL with DEFAULT 0, and 03.01.01 restates
        // it: L991-999 drops the default constraint by looking its name up dynamically, L1001
        // re-asserts the column as NOT NULL and L1003 re-creates the default. Unlike the column
        // above, this default survives to the terminal schema and is reproduced, so an insert that
        // omits the column lands on zero exactly as the database would decide rather than failing.
        //
        // MIGRATION: -1 is a REAL STORED VALUE on this column, never an absent one, so no value
        //   converter and no nullable widening is applied. The legacy screen at
        //   Website/admin/Modules/ModuleSettings.ascx.vb lines 138-142 hides its cache-time field
        //   when the value equals the legacy integer sentinel of -1. Because the column is NOT NULL
        //   with a default of 0, a stored -1 cannot be an artefact of a database null - it was
        //   written deliberately - so translating it to null in this layer would destroy the
        //   distinction between it and a genuine 0 and would change what an administrator sees.
        builder.Property(m => m.DefaultCacheTime)
            .HasColumnName("DefaultCacheTime")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // MIGRATION: uniqueness here is the mirror image of the sibling table's, and the two must
        //   not be conflated. 01.00.08:L5867-5872 adds CONSTRAINT IX_ModuleDefinitions UNIQUE
        //   NONCLUSTERED (FriendlyName) to this table and no later script drops it, so a friendly
        //   name is unique across every definition in the installation - which is what makes it the
        //   natural key that GetModuleDefinitionByName resolves against
        //   (DataProvider.vb line 172). The equivalent constraint on dbo.DesktopModules was dropped
        //   by 03.01.00 and replaced with a plain index, so a friendly name is NOT unique among
        //   packages. Asserting uniqueness the database lacks would make the change tracker reject
        //   duplicates the database accepts; failing to assert the uniqueness that does exist would
        //   let a duplicate reach the database and fail there instead.
        builder.HasIndex(m => m.FriendlyName)
            .IsUnique()
            .HasDatabaseName("IX_ModuleDefinitions");

        // 02.00.00:L5269-5270 - create index IX_ModuleDefinitions_1 on ModuleDefinitions
        // (DesktopModuleID). Deliberately not unique: one package publishes many definitions, which
        // is the entire point of the 02.00.00 split.
        builder.HasIndex(m => m.DesktopModuleId)
            .HasDatabaseName("IX_ModuleDefinitions_1");

        // 02.00.00:L5258-5265 - CONSTRAINT FK_ModuleDefinitions_DesktopModules FOREIGN KEY
        // (DesktopModuleID) REFERENCES DesktopModules (DesktopModuleID) ON DELETE CASCADE NOT FOR
        // REPLICATION. The cascade is described as the existing schema declares it rather than
        // softened: deleting a package already removes its definitions in the legacy database.
        // NOT FOR REPLICATION has no counterpart in the model and is left unrepresented across this
        // folder. The foreign key reuses the column mapped above, so no shadow property appears.
        builder.HasOne(m => m.DesktopModule)
            .WithMany(d => d.ModuleDefinitions)
            .HasForeignKey(m => m.DesktopModuleId)
            .HasConstraintName("FK_ModuleDefinitions_DesktopModules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
