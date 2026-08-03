using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="PortalDesktopModule"/> join entity to the existing, immutable DotNetNuke 4.9
/// table <c>dbo.PortalDesktopModules</c>.
/// </summary>
/// <remarks>
/// <para>
/// One row is one grant: it entitles a single portal to use a single installed module package. A
/// grant holds no state beyond the pair it joins, so this mapping is short - and every line of it is
/// load-bearing, because none of the three column names, none of the four constraint names and
/// neither delete behaviour can be reached by convention.
/// </para>
/// <para>
/// This is the one genuinely stable table in the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>. It is created at
/// <c>02.02.02:L3028-L3033</c> with exactly the three columns mapped here, and four
/// <c>ADD CONSTRAINT</c> statements follow it in the same script - the primary key at
/// <c>:L3036-L3040</c>, the unique pair at <c>:L3044-L3049</c>, and the two foreign keys at
/// <c>:L3053-L3061</c> and <c>:L3065-L3073</c>. No later script in the chain touches the table's
/// structure at all: the only other statements that name it are three <c>DROP CONSTRAINT</c> lines
/// in <c>UnInstall.SqlDataProvider</c>, which run when DotNetNuke is being removed, and three
/// read-only joins in <c>03.02.00</c>, <c>04.03.06</c> and <c>04.05.00</c>. The created shape is
/// therefore also the terminal shape, which is rare in this chain and is recorded here so that
/// nobody hunts for widenings that do not exist.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything:
/// the mapping describes a table that already exists so that Entity Framework can address it, and
/// the baseline migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// <para>
/// The key is a surrogate over a natural key the schema already enforces. The joined pair is unique,
/// so the pair alone would have served as a composite primary key; the surrogate exists because the
/// legacy insert procedure returns <c>SCOPE_IDENTITY()</c> to its caller
/// (<c>02.02.02:L3111-L3127</c>). Both are mapped - the surrogate as the key, the pair as a unique
/// index under its legacy name - so the database keeps enforcing the rule instead of a service
/// re-implementing it.
/// </para>
/// <para>
/// Both foreign keys cascade on delete in the schema and both are reproduced as such. Deleting a
/// portal, or uninstalling a package, withdraws the grants that referenced it, which is the only
/// coherent behaviour for a join row whose entire content is the pair it joins: a grant to a portal
/// that no longer exists has nothing left to mean. The reasoning for declaring two cascades into one
/// dependent is recorded at the two calls themselves.
/// </para>
/// <para>
/// This type declares no constructor. <c>DnnDbContext.OnModelCreating</c> discovers configurations
/// with <c>ApplyConfigurationsFromAssembly</c>, which instantiates each candidate through its public
/// parameterless constructor. An <c>internal sealed</c> class with no declared constructor receives
/// a compiler-generated public one and is found; declaring a non-public parameterless constructor
/// would make this class silently undiscoverable, with no compile error and no model-validation
/// error to reveal it. The consequence would not be a missing table - SQL Server resolves
/// identifiers case-insensitively and the context's set property already supplies the plural name -
/// but a quietly wrong model: the primary key, the unique pair and both foreign keys would lose
/// their legacy names, and the pair would lose its uniqueness altogether, so the single rule this
/// table exists to enforce would stop being enforced.
/// </para>
/// </remarks>
internal sealed class PortalDesktopModuleConfiguration : IEntityTypeConfiguration<PortalDesktopModule>
{
    /// <summary>
    /// Applies the mapping for the portal-to-package grant entity type.
    /// </summary>
    /// <param name="builder">The builder for the grant entity type.</param>
    public void Configure(EntityTypeBuilder<PortalDesktopModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy PortalDesktopModuleInfo object and this table are not the same shape,
        // and only the table is authoritative here. The legacy class
        // [Library/Components/Modules/PortalDesktopModuleInfo.vb:L30-L81] declared five properties;
        // the table has three columns [02.02.02:L3028-L3033]. The two extra properties held display
        // text lifted from the principal tables by a join, which the legacy read procedure proves:
        // GetPortalDesktopModules selects "PortalDesktopModules.*" plus one name taken from
        // dbo.Portals and one taken from dbo.DesktopModules across two inner joins
        // [02.02.02:L3155-L3160], and the reflection-based row hydrator then filled all five
        // properties indiscriminately - which is precisely how a result-set shape came to be mistaken
        // for a row shape.
        //
        // MIGRATION: neither of those two names is mapped, and neither may ever be added as a scalar,
        // because no such column exists on this table and a mapping for one would fail on the first
        // read of a real installation. The domain entity declares neither, so there is nothing to
        // exclude either. A caller that needs display text composes it in the Application layer from
        // the two references declared at the end of this method, whose principals own those values.

        // The legacy provider registration runs with an empty object qualifier
        // [Website/release.config:L354] and "dbo" as the database owner [:L355], so the physical name
        // is unqualified and the schema is dbo. Both are stated rather than inferred: convention
        // would take the name from the context's set property, which happens to match, and would
        // place the table in the connection's default schema, which is only dbo by luck. Neither
        // coincidence is a contract.
        builder.ToTable("PortalDesktopModules", "dbo");

        // PK_PortalDesktopModules PRIMARY KEY CLUSTERED (PortalDesktopModuleID)
        // [02.02.02:L3036-L3040]. The legacy name is preserved so that the constraint the model
        // describes is the constraint the live database already has.
        builder.HasKey(g => g.PortalDesktopModuleId).HasName("PK_PortalDesktopModules");

        // The database assigns this value: the legacy insert supplies only the two foreign keys and
        // then returns SCOPE_IDENTITY() [02.02.02:L3111-L3127]. Seed and increment are both 1
        // [02.02.02:L3030], so both are pinned rather than assumed, and a script generated from this
        // model reproduces the real column. The column spells its suffix in full upper case, which no
        // C# naming convention would produce from this member, so it is named explicitly.
        builder.Property(g => g.PortalDesktopModuleId)
            .HasColumnName("PortalDesktopModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: all three columns are NOT NULL [02.02.02:L3030-L3032], so no nullable-sentinel
        // question arises on this table at all. There is no null to fold into the legacy markers for
        // an absent integer, an absent string, an absent date or an absent byte, and no value
        // converter is declared in either direction. The opposite hazard does arise, and it is the
        // one that bites: dbo.Portals.PortalID is declared IDENTITY(-1, 1), so -1 and 0 are both
        // legitimate, persisted portal identifiers, while dbo.DesktopModules.DesktopModuleID is
        // declared IDENTITY(1, 1). A PortalID of -1 or 0 on a grant row is therefore real data naming
        // a real portal - never "absent", "unset" or "not yet saved" - and no code above this mapping
        // may read it as such.
        //
        // Every column is a plain int with no default constraint in the terminal schema, and the
        // table carries no text column and no temporal column whatsoever, so this file declares no
        // length, no collation and no store default anywhere.
        builder.Property(g => g.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(g => g.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .IsRequired();

        // ALTER TABLE PortalDesktopModules ADD CONSTRAINT IX_PortalDesktopModules UNIQUE NONCLUSTERED
        // (PortalID, DesktopModuleID) [02.02.02:L3044-L3049], never dropped by any upgrade script.
        // Uniqueness is the entire semantic purpose of the table: it is what stops one package being
        // granted twice to the same portal, so relaxing the flag would quietly demote a
        // database-enforced rule to an unenforced convention. The column order is the schema's -
        // PortalID first [:L3047], DesktopModuleID second [:L3048] - and it matters, because an index
        // serves only a leading subset of its key columns and every read of this table filters by
        // portal first.
        builder.HasIndex(g => new { g.PortalId, g.DesktopModuleId })
            .IsUnique()
            .HasDatabaseName("IX_PortalDesktopModules");

        // MIGRATION: both terminal foreign keys are declared ON DELETE CASCADE and NOT FOR
        // REPLICATION - to dbo.DesktopModules at [02.02.02:L3053-L3061] and to dbo.Portals at
        // [02.02.02:L3065-L3073] - and both cascades are reproduced faithfully under their legacy
        // constraint names. Downgrading either one to a no-action or a restricting behaviour, to
        // dodge the familiar "multiple cascade paths" complaint, would invent schema semantics this
        // migration is forbidden to change and would strand rows that the live database removes
        // today.
        //
        // MIGRATION: two cascades reaching one dependent are safe here for two independent reasons.
        // Entity Framework's model validator does not reject multiple cascade paths - only its
        // migration generator objects - and the baseline migration in this solution is intentionally
        // empty, so no generator is ever pointed at this model. The mapping's job is to describe the
        // database exactly as it is. NOT FOR REPLICATION has no Entity Framework equivalent and is
        // deliberately left unrepresented, as it is everywhere else in this folder.
        builder.HasOne(g => g.Portal)
            .WithMany(portal => portal.PortalDesktopModules)
            .HasForeignKey(g => g.PortalId)
            .HasConstraintName("FK_PortalDesktopModules_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.DesktopModule)
            .WithMany(package => package.PortalDesktopModules)
            .HasForeignKey(g => g.DesktopModuleId)
            .HasConstraintName("FK_PortalDesktopModules_DesktopModules")
            .OnDelete(DeleteBehavior.Cascade);

        // Both relationships are configured here and nowhere else. This entity is the dependent of
        // both principals - it holds both foreign-key columns - so it owns both relationships, and
        // PortalConfiguration and DesktopModuleConfiguration each deliberately declare none.
        // Restating either from a principal side would define the same relationship twice, and
        // because ApplyConfigurationsFromAssembly guarantees no ordering, the surviving delete
        // behaviour would be whichever call happened to run last, with no compile error and no
        // model-validation error to reveal it.
        //
        // Nothing further is declared. Entity Framework supplies its own index over DesktopModuleID
        // by convention, because the unique pair above already leads with PortalID and so covers that
        // column; that convention index is the physical IX_PortalDesktopModules_DesktopModuleID, and
        // a second explicit index here would only duplicate it. Entity<int>.Identity is likewise not
        // excluded by hand: it is a get-only expression-bodied property over PortalDesktopModuleId,
        // and property discovery requires a writable property, so it is never a mapping candidate in
        // the first place.
    }
}
