using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="RoleGroup"/> entity to the legacy <c>dbo.RoleGroups</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Role groups are purely organisational: a group gathers roles for presentation in the
/// administrative lists and carries no permission of its own, because permissions are granted to a
/// role and never to its group. Group membership is optional for a role, so an ungrouped role is
/// entirely normal.
/// </para>
/// <para>
/// This table is the one welcome simplicity in an otherwise destructive upgrade chain. It arrived
/// complete and was never widened: across the eighty-seven installable scripts under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> the only statements that touch it as a
/// table are the four inside a single existence-guarded block, and that block is issued twice -
/// <c>03.02.03:L16-31</c> and, identically, <c>04.00.04:L49-64</c>. No statement anywhere adds,
/// drops or re-types a column of it, drops a constraint of it, or drops the table. The
/// table-creation block is therefore the terminal definition, and every store type, length,
/// nullability and constraint name below is cited to it directly. Contrast <c>dbo.Portals</c>, whose
/// terminal shape emerges only after sixteen column additions, three column drops, a full table
/// rebuild through a temporary copy and eight re-typings, none of which can be skipped without
/// getting the mapping wrong.
/// </para>
/// <para>
/// The schema is read-only truth. Nothing here creates, alters or drops anything: this mapping
/// describes a table that already exists so that Entity Framework can address it, and the baseline
/// migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// <para>
/// The terminal shape is four columns and three objects:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Property and column</term>
///     <description>Terminal definition and consequence</description>
///   </listheader>
///   <item>
///     <term><see cref="RoleGroup.RoleGroupId"/> maps <c>RoleGroupID</c></term>
///     <description>
///     <c>int IDENTITY(0,1) NOT NULL</c> (<c>04.00.04:L51</c>), the primary key under
///     <c>PK_RoleGroups</c>. Seeded at zero, so zero is a real key - see the migration note on the
///     property.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RoleGroup.PortalId"/> maps <c>PortalID</c></term>
///     <description>
///     <c>int NOT NULL</c> (<c>04.00.04:L52</c>), the required tenant reference under
///     <c>FK_RoleGroups_Portals</c>. Required here and nullable on the neighbouring tables - again,
///     see the migration note on the property.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RoleGroup.RoleGroupName"/> maps <c>RoleGroupName</c></term>
///     <description>
///     <c>nvarchar</c> of width 50, <c>NOT NULL</c> (<c>04.00.04:L53</c>), the administrative
///     display name.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RoleGroup.Description"/> maps <c>Description</c></term>
///     <description>
///     <c>nvarchar</c> of width 1000, <c>NULL</c> (<c>04.00.04:L54</c>) - the one nullable column of
///     the table, and one thousand characters rather than the five hundred used by the description
///     columns of <c>dbo.Portals</c> and <c>dbo.Tabs</c>.
///     </description>
///   </item>
/// </list>
/// <para>
/// Both text columns are Unicode and neither is fixed width, so no store type is narrowed to ANSI
/// and none is declared fixed length. No column of this table carries a database default, so none is
/// configured: inventing one would let Entity Framework omit a supplied value from an insert and
/// have the database substitute a different one.
/// </para>
/// <para>
/// This type declares no constructor. <c>DnnDbContext.OnModelCreating</c> discovers configurations
/// with <c>ApplyConfigurationsFromAssembly</c>, which instantiates each candidate through its public
/// parameterless constructor. An <c>internal sealed</c> class with no declared constructor receives a
/// compiler-generated public one and is found; declaring a non-public parameterless constructor
/// would make this class silently undiscoverable, with no compile error and no model-validation
/// error, and the entity would fall back to convention mapping - losing the unique
/// <c>(PortalID, RoleGroupName)</c> constraint, the identity seed and the cascade, and binding to a
/// table named <c>RoleGroup</c> that does not exist.
/// </para>
/// </remarks>
internal sealed class RoleGroupConfiguration : IEntityTypeConfiguration<RoleGroup>
{
    /// <summary>
    /// Applies the mapping for the role group entity type.
    /// </summary>
    /// <param name="builder">The builder for the role group entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<RoleGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: every citation below is doubled, and that is a property of the source rather
        // than duplicated evidence. The entire definition of this table is issued twice by the
        // upgrade chain - 03.02.03:L16-31 and, with a byte-identical body, 04.00.04:L49-64 - each
        // time wrapped in the same IF NOT EXISTS ... OBJECTPROPERTY(id, N'IsTable') = 1 guard. The
        // later script is a consolidated re-issue for installations that skipped the earlier one, so
        // whichever runs first creates the table and the other is a no-op. The two are therefore ONE
        // logical change, not two, and no installation can end up with a different shape depending on
        // its upgrade path. Because nothing afterwards alters or drops the table, that single block is
        // the terminal definition and is quoted directly by each annotation that follows; the
        // 04.00.04 line number is given first because it is the form present in every currently
        // supported installation.
        //
        // The legacy provider registration uses an empty object qualifier and "dbo" as the database
        // owner (Website/release.config:L354-L355), so the table is unprefixed and lives in dbo. The
        // name is genuinely plural here, unlike the singular dbo.Permission, dbo.PortalAlias,
        // dbo.TabPermission and dbo.ModulePermission mapped by the sibling configurations.
        builder.ToTable("RoleGroups", "dbo");

        // MIGRATION: PK_RoleGroups is declared NONCLUSTERED (04.00.04:L57-58, identical to
        // 03.02.03:L24-25) and is never dropped. Clustering is physical storage metadata with no
        // Entity Framework surface, so it is recorded here in comment form rather than expressed in
        // code; the constraint NAME is carried into the model because that does have a surface.
        builder.HasKey(g => g.RoleGroupId).HasName("PK_RoleGroups");

        // MIGRATION: RoleGroupID is int IDENTITY(0,1) NOT NULL (04.00.04:L51, identical to
        // 03.02.03:L18). THE SEED IS ZERO, so RoleGroupId 0 is a real, valid, saved group and must
        // never be read as "absent", "unset", "default" or "not yet persisted". Nothing may test
        // `RoleGroupId == 0`, `<= 0` or `== default`, and no IsNew or IsTransient notion may be
        // inferred from the key: whether a row exists is DECLARED through
        // Entity<int>.MarkIdentityPersisted and read back through IdentityIsPersisted.
        //
        // This is the schema-wide hazard, not a quirk of one table. Roles.RoleID, Modules.ModuleID
        // and Tabs.TabID are all IDENTITY(0,1), and Portals.PortalID is IDENTITY(-1,1) - so BOTH of
        // the legacy sentinel-adjacent values, 0 and -1, are legitimate primary keys somewhere in
        // this database. The legacy Null.vb table (NullInteger = -1, NullString = "", NullDate =
        // DateTime.MinValue, NullByte = 255) therefore collides with real data, which is precisely
        // why no value converter anywhere in persistence folds a sentinel into null or null into a
        // sentinel in either direction. Sentinel semantics survive only at the DTO and API boundary,
        // where they are externally observable.
        //
        // The seed and increment are preserved in the model rather than left to default to (1,1) so
        // that a script generated from this model would reproduce the real table.
        builder.Property(g => g.RoleGroupId)
            .HasColumnName("RoleGroupID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // MIGRATION: PortalID is int NOT NULL on this table (04.00.04:L52, identical to
        // 03.02.03:L19), which DIVERGES from its immediate neighbours: Roles.PortalID and
        // Tabs.PortalID are both int NULL, because a role and a page may exist at host level with no
        // owning tenant. A role group has no host-level form - every group is portal-scoped - so the
        // property is a non-nullable int and the column is pinned required rather than inheriting the
        // nullable treatment of RoleConfiguration or TabConfiguration.
        //
        // Note also that Portals.PortalID is IDENTITY(-1,1), so a PortalId of -1 or 0 is a real
        // portal reference and neither may be treated as "no portal".
        builder.Property(g => g.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar of width 50, NOT NULL (04.00.04:L53). Unicode and variable width: the store type
        // is neither narrowed to ANSI nor declared fixed length, because this table has no ANSI and
        // no fixed-width column to narrow it to.
        builder.Property(g => g.RoleGroupName)
            .HasColumnName("RoleGroupName")
            .HasMaxLength(50)
            .IsRequired();

        // nvarchar of width 1000, NULL (04.00.04:L54) - the only nullable column of the table, so
        // this is the only property left optional. One thousand characters, matching
        // Roles.Description, not the five hundred of Portals.Description and Tabs.Description.
        builder.Property(g => g.Description)
            .HasColumnName("Description")
            .HasMaxLength(1000);

        // MIGRATION: the uniqueness rule is declared as a UNIQUE NONCLUSTERED table CONSTRAINT
        // rather than a CREATE UNIQUE INDEX statement (04.00.04:L60-61, identical to
        // 03.02.03:L27-28). Entity Framework expresses both forms identically as a unique index, so
        // the difference is one of DDL spelling only and the physical name is carried through
        // verbatim.
        //
        // Two details are load-bearing. First, the key spans (PortalID, RoleGroupName) IN THAT
        // ORDER, so a group name is unique WITHIN ITS PORTAL and not across the installation: two
        // tenants may each own a group of the same name, and any uniqueness check that omits the
        // portal would reject a legitimate insert. Second, the constraint is named IX_RoleGroupName -
        // singular RoleGroup, with no trailing "s" - even though the table is plural. That
        // inconsistency is in the shipped schema, so it is reproduced rather than corrected.
        builder.HasIndex(g => new { g.PortalId, g.RoleGroupName })
            .IsUnique()
            .HasDatabaseName("IX_RoleGroupName");

        // MIGRATION: FK_RoleGroups_Portals carries an explicit ON DELETE CASCADE (04.00.04:L63-64,
        // identical to 03.02.03:L30-31), so deleting a portal deletes its role groups with it. That
        // real cascade is reproduced faithfully and is NOT downgraded to NoAction to avoid multiple
        // cascade paths: Entity Framework's model validator does not reject multiple cascade paths
        // when the model is built - only the migration generator objects - and the baseline migration
        // is deliberately empty, so nothing here ever generates one. Weakening the behaviour would
        // instead orphan rows that the database itself removes.
        //
        // The inverse below is deliberately left unspecified: the Portal entity declares collection
        // navigations for aliases, modules, tabs, roles, user portals and portal desktop modules, but
        // none for role groups. That is the entity's own contract and this file does not alter the
        // Domain layer to widen it, so the relationship is configured from the dependent side with no
        // inverse navigation. The foreign key binds to the PortalID column mapped above, so no shadow
        // property is introduced.
        //
        // WITH NOCHECK, which the constraint also carries, suppresses validation of rows already
        // present at the time the constraint was added. It has no Entity Framework equivalent and is
        // deliberately unrepresented, consistently with every sibling configuration in this folder.
        //
        // The relationship is required, and that is derived from PortalId being a non-nullable int
        // rather than asserted again here; marking the relationship builder itself required would
        // only restate what the property nullability already fixes.
        builder.HasOne(g => g.Portal)
            .WithMany()
            .HasForeignKey(g => g.PortalId)
            .HasConstraintName("FK_RoleGroups_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: the inbound relationship from the Roles table is deliberately ABSENT from this
        // file. A role group is the principal of a role - Roles.RoleGroupID is added as int NULL at
        // 04.00.04:L66-67 and constrained by FK_Roles_RoleGroups at 04.00.04:L69-70 - but that
        // constraint carries NO ON DELETE clause and is therefore NoAction, and it is configured
        // exactly once, in RoleConfiguration, on the dependent side where the foreign key lives.
        //
        // This is not a stylistic preference. ApplyConfigurationsFromAssembly gives no ordering
        // guarantee, so a relationship configured from both ends resolves to whichever delete
        // behaviour happened to run last - with no compile error and no model-validation error to
        // reveal it. Declaring the principal side of that same relationship here - a collection
        // mapping from this group over its roles - could therefore silently replace a deliberate
        // NoAction with a cascade and, combined with the portal cascade above, turn deleting one
        // portal into a deletion of roles the database would have refused to remove. Every
        // relationship in this folder is configured once, in the dependent's configuration, for
        // exactly that reason, and no principal-side collection mapping appears anywhere in it.
        //
        // The asymmetry is genuine legacy behaviour and is preserved rather than smoothed over:
        // deleting a portal removes its groups by cascade, but deleting a group on its own does not
        // remove or detach the roles pointing at it - those roles must be reassigned or cleared
        // first, or the database refuses the delete.
    }
}
