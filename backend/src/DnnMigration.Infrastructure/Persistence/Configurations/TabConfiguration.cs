using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Tab"/> aggregate to the existing, immutable DotNetNuke 4.9 table
/// <c>dbo.Tabs</c>: the page abstraction that module placement, page permissions and portal
/// navigation are all keyed by.
/// </summary>
/// <remarks>
/// <para>
/// Every column name, store type, length, nullability and default below is taken from the
/// <em>terminal</em> state of the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, never from the baseline creation script
/// alone. That distinction is load-bearing here: the baseline created ten columns, the chain then
/// added eighteen more and dropped six again, so a configuration derived from the first script would
/// bind five columns that no longer exist and would miss twelve that do. The arithmetic resolves to
/// exactly the twenty-two columns mapped here, a count corroborated independently by the select list
/// of the terminal <c>vw_Tabs</c> view (<c>04.05.04:L52-L87</c>), and <c>04.05.04</c> is the last
/// script in the whole chain to alter this table at all.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything:
/// the mapping describes a table that already exists so that Entity Framework can address it, and
/// the baseline migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// <para>
/// Three column names cannot be reached by convention and are therefore pinned explicitly.
/// <c>TabID</c> capitalises its suffix; <c>KeyWords</c> carries a capital <c>W</c> that the
/// modernised member <see cref="Tab.Keywords"/> does not; and <c>ParentId</c> carries a lower-case
/// <c>d</c> that the stored procedure parameters spelled <c>@ParentID</c> and the terminal index
/// definition (<c>03.00.09:L350</c>) spelled <c>ParentId</c>. SQL Server resolves identifiers
/// case-insensitively, which is precisely why these differences are easy to miss and impossible to
/// rely on. Modernising a member name must never rename a column.
/// </para>
/// <para>
/// Store defaults are recorded for fidelity, but a default must never be allowed to overwrite a
/// value the caller actually supplied. Entity Framework omits a property from an INSERT when the
/// property still holds its sentinel and a store default is configured, letting the database supply
/// the value instead. That is harmless for the four columns whose store default equals the CLR
/// default. It is <em>not</em> harmless for <c>IsVisible</c>, whose store default is <c>1</c> while
/// the CLR default is <see langword="false"/> - and a hidden page is an ordinary thing to ask for -
/// so that column is pinned with <c>ValueGeneratedNever</c> and is therefore always written
/// explicitly. The same technique is applied to <c>HostFee</c> and <c>TimezoneOffset</c> in
/// <see cref="PortalConfiguration"/> for the same reason.
/// </para>
/// <para>
/// This type declares no constructor. <c>DnnDbContext.OnModelCreating</c> discovers configurations
/// with <c>ApplyConfigurationsFromAssembly</c>, which instantiates each candidate through its public
/// parameterless constructor. An <c>internal sealed</c> class with no declared constructor receives
/// a compiler-generated public one and is found; declaring a non-public parameterless constructor
/// would make this class silently undiscoverable, with no compile error and no model-validation
/// error. For this entity the consequence would be specific and severe: the self-reference below
/// would go unconfigured, Entity Framework would infer it by convention with a cascading delete, and
/// the resulting self-referencing cascade path is one SQL Server cannot implement.
/// </para>
/// </remarks>
internal sealed class TabConfiguration : IEntityTypeConfiguration<Tab>
{
    /// <summary>
    /// Applies the mapping for the tab entity type.
    /// </summary>
    /// <param name="builder">The builder for the tab entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Tab> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy Library/Components/Tabs/TabInfo.vb (616 lines) mixed persisted
        // columns with derived state, declaring thirty-six public members of which only the
        // twenty-two below are columns. HasChildren (:L291) is an EXISTS projection in vw_Tabs
        // (04.05.04:L83); SkinPath (:L347), ContainerPath (:L356), BreadCrumbs (:L365), Panes
        // (:L374) and Modules (:L383) are page-rendering state; IsSuperTab (:L392), TabType
        // (:L406), FullUrl (:L412) and IsAdminTab (:L435) are computed - IsAdminTab even issues a
        // database read from its getter. None of them is a column, so none of them is mapped. The
        // type also declared "Implements IPropertyAccess" (:L41), whose GetProperty accessor
        // (:L524) and Cacheability member (:L605) are dropped with the excluded token-replacement
        // subsystem, and it carried XML-serialisation attributes on every member: the wire contract
        // belongs to the DTOs at the API boundary, and this file is the only place the storage
        // contract is stated.

        // objectQualifier is empty and databaseOwner is "dbo" in the legacy provider registration
        // (Website/release.config:L354-L355), so the table is unprefixed and lives in dbo.
        builder.ToTable("Tabs", "dbo");

        // PK_Tabs PRIMARY KEY NONCLUSTERED (TabID), declared alongside DF_Tabs_IsVisible at
        // 01.00.00:L496-L502. The chain never drops or rebuilds it.
        builder.HasKey(t => t.TabId).HasName("PK_Tabs");

        // MIGRATION: TabID is IDENTITY(0, 1) [01.00.00:L140], so the FIRST REAL TAB IS IDENTIFIED
        // BY ZERO. Zero is a legitimate, persisted page identifier and must never be read as
        // "absent", "unset" or "not yet saved", despite the legacy Null.NullInteger idiom that
        // reserved -1 for absence. The same trap applies one column over: ParentId = 0 means
        // "child of the tab identified 0", whereas a root page has ParentId = NULL. Conflating the
        // two would silently reparent pages and corrupt the hierarchy. The seed is preserved in the
        // model rather than defaulted to 1 so that a script generated from this model reproduces
        // the real table.
        builder.Property(t => t.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // int NOT NULL [01.00.00:L141], tightened at 03.01.01:L1278 with DF_Tabs_TabOrder DEFAULT
        // (0) at 01.00.05:L982-L983 and re-added at 03.01.01:L1284. The store default equals the
        // CLR default, so letting the database supply it cannot change a caller's value.
        builder.Property(t => t.TabOrder)
            .HasColumnName("TabOrder")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // MIGRATION: nullable SQL columns map to nullable CLR properties and nothing else. The
        // legacy Null.vb sentinel table - NullInteger = -1, NullString = "", NullDate =
        // DateTime.MinValue, NullByte = 255 - is honoured here as mapping knowledge only: no value
        // converter folds null into a sentinel or a sentinel into null, in either direction.
        // Sentinel semantics survive only at the DTO and API boundary, where they are externally
        // observable. PortalID is int NULL [01.00.00:L142] and its nullability is load-bearing: a
        // null identifies a host-level tab that belongs to the installation rather than to any
        // portal, and because dbo.Portals.PortalID is IDENTITY(-1, 1) the value -1 is a real portal
        // key rather than an absence. No IsRequired here.
        builder.Property(t => t.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // The only non-nullable string on the table: nvarchar(50) NOT NULL [01.00.00:L143].
        builder.Property(t => t.TabName)
            .HasColumnName("TabName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: bit NOT NULL [01.00.00:L149], tightened at 03.01.01:L1279, and the ONLY bit on
        // this table whose store default is true - DF_Tabs_IsVisible DEFAULT (1) at 01.00.00:L497,
        // re-added at 03.01.01:L1286. The default is recorded for schema fidelity, and
        // ValueGeneratedNever then guarantees the column is always written, so a caller asking for
        // a hidden page stores false rather than having the column omitted from the INSERT and
        // silently defaulted back to visible.
        builder.Property(t => t.IsVisible)
            .HasColumnName("IsVisible")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // int NULL [01.00.04:L46-L47] - the column carries a lower-case d, which the terminal index
        // definition at 03.00.09:L350 confirms. Nullable and deliberately not required: a null is a
        // root-level page. See the TabID note above for why zero is not an alternative spelling of
        // that.
        builder.Property(t => t.ParentId)
            .HasColumnName("ParentId")
            .HasColumnType("int");

        // int NOT NULL CONSTRAINT DF_Tabs_Level DEFAULT 0 [01.00.05:L977-L978], tightened at
        // 03.01.01:L1280 with the default re-added at L1288. A denormalised depth maintained by the
        // write path; Level is a keyword-adjacent identifier that the schema brackets, and Entity
        // Framework quotes every identifier it emits, so naming it plainly is safe.
        builder.Property(t => t.Level)
            .HasColumnName("Level")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // nvarchar(100) NULL [01.00.05:L979]. The raw base-table value is mapped, which for rows
        // written through the legacy file manager is the token "fileid=N" rather than a path: the
        // terminal view resolves it by joining dbo.Files (04.05.04:L62-L71), a table this migration
        // does not model, and baking that projection in would write a resolved path back over the
        // token on the next save.
        builder.Property(t => t.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // bit, added NOT NULL DEFAULT (0) at 01.00.10:L277. Its default constraint was dropped by
        // dynamic name discovery at 03.00.09:L338-L341, the column re-asserted NOT NULL at L347 and
        // DF_Tabs_DisableLink DEFAULT (0) re-added at L352, then re-asserted once more at
        // 03.01.01:L1281 and L1290.
        builder.Property(t => t.DisableLink)
            .HasColumnName("DisableLink")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // nvarchar(200) NULL [02.00.00:L4752].
        builder.Property(t => t.Title)
            .HasColumnName("Title")
            .HasMaxLength(200);

        // nvarchar(500) NULL [02.00.00:L4753].
        builder.Property(t => t.Description)
            .HasColumnName("Description")
            .HasMaxLength(500);

        // MIGRATION: the member is spelled Keywords with a lower-case w while the column it binds
        // to is spelled KeyWords with a capital W [02.00.00:L4754]. This explicit HasColumnName is
        // the single place that difference is reconciled. Note the deliberate asymmetry against
        // Portal, whose member is spelled KeyWords and which maps to a column of the SAME name on
        // its own table: neither side is "corrected" to match the other, because the member names
        // came from two different legacy classes and the column names are fixed by the shipped
        // schema.
        builder.Property(t => t.Keywords)
            .HasColumnName("KeyWords")
            .HasMaxLength(500);

        // bit NOT NULL CONSTRAINT DF_Tabs_IsDeleted DEFAULT 0 [02.00.00:L4755], tightened at
        // 03.01.01:L1282 with the default re-added at L1292. A soft delete: the row survives and
        // read paths that present live pages filter on this flag.
        builder.Property(t => t.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // nvarchar(255) NULL [02.02.00:L39-L40]. The column is spelled Url, not URL; the terminal
        // view aliases the same column as URL at 04.05.04:L82, which is the identical column under
        // a case-insensitive identifier.
        builder.Property(t => t.Url)
            .HasColumnName("Url")
            .HasMaxLength(255);

        // nvarchar(200) NULL [02.02.02:L2541]. Preserved so a row round-trips intact even though
        // the skinning subsystem that consumed it lies outside the scope of this migration.
        builder.Property(t => t.SkinSrc)
            .HasColumnName("SkinSrc")
            .HasMaxLength(200);

        // nvarchar(200) NULL [02.02.02:L2542].
        builder.Property(t => t.ContainerSrc)
            .HasColumnName("ContainerSrc")
            .HasMaxLength(200);

        // nvarchar(255) NULL [02.02.02:L3185]. A denormalised copy of the parent chain, stored
        // rather than recomputed on read.
        builder.Property(t => t.TabPath)
            .HasColumnName("TabPath")
            .HasMaxLength(255);

        // datetime NULL [02.02.02:L3186] - the legacy SQL Server datetime type, pinned explicitly
        // because the modern successor type is what convention would otherwise choose. The two
        // differ in range, precision and storage, and binding the wrong one changes how values
        // round-trip against an existing installation.
        builder.Property(t => t.StartDate)
            .HasColumnName("StartDate")
            .HasColumnType("datetime");

        // datetime NULL [02.02.02:L3187], on the same terms as StartDate.
        builder.Property(t => t.EndDate)
            .HasColumnName("EndDate")
            .HasColumnType("datetime");

        // int NULL [03.01.01:L408-L409].
        builder.Property(t => t.RefreshInterval)
            .HasColumnName("RefreshInterval")
            .HasColumnType("int");

        // nvarchar(500) NULL [03.01.01:L410].
        builder.Property(t => t.PageHeadText)
            .HasColumnName("PageHeadText")
            .HasMaxLength(500);

        // bit NOT NULL CONSTRAINT DF_Tabs_IsSecure DEFAULT (0) [04.05.04:L45-L46]. This is the last
        // column the chain ever adds to this table, in the terminal script, and the terminal view
        // selects it at L86. Every earlier table definition lacks it, so a mapping reconstructed
        // from any script but the last would silently lose a real column.
        builder.Property(t => t.IsSecure)
            .HasColumnName("IsSecure")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // MIGRATION: six columns present earlier in the chain are absent from the terminal schema
        // and are deliberately unmapped: LeftPaneWidth [dropped 02.00.00:L4735-L4736],
        // RightPaneWidth [:L4739-L4740], MobileTabName [:L4743-L4744], ShowMobile [:L4747-L4748],
        // AdministratorRoles [added 01.00.06:L593-L594, dropped 03.00.01:L1407-L1408] and
        // AuthorizedRoles [dropped 03.00.01:L1410-L1411]. The last two held the legacy
        // semicolon-delimited role strings; page authority now lives as rows in dbo.TabPermission,
        // reached through Tab.TabPermissions, and is never read from a delimited string.

        // FK_Tabs_Portals FOREIGN KEY (PortalID) REFERENCES dbo.Portals (PortalID) ON DELETE
        // CASCADE NOT FOR REPLICATION - created at 01.00.00:L607-L614, dropped at 01.00.05:L1418-
        // L1419 and re-added with the same cascade at 01.00.05:L1540-L1547. The only later mention
        // is a no-op sp_rename at 02.00.00:L153. The cascade is declared here so the tracked graph
        // behaves in memory exactly as the database does. The foreign-key column is nullable, so
        // the relationship is optional and is not forced required: a host-level tab has no portal.
        //
        // Every relationship is configured exactly once, from the DEPENDENT side. Tab holds both
        // foreign keys, so this file owns both of the relationships below and no other file
        // restates them; the two inverse collections Tab.TabModules and Tab.TabPermissions are
        // owned by TabModuleConfiguration and TabPermissionConfiguration respectively, and
        // PortalConfiguration deliberately declares none. Because
        // ApplyConfigurationsFromAssembly guarantees no ordering, a relationship configured from
        // both ends would keep whichever delete behaviour ran last, with no compile error and no
        // model-validation error to reveal it.
        builder.HasOne(t => t.Portal)
            .WithMany(p => p.Tabs)
            .HasForeignKey(t => t.PortalId)
            .HasConstraintName("FK_Tabs_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: FK_Tabs_Tabs FOREIGN KEY (ParentId) REFERENCES dbo.Tabs (TabID) NOT FOR
        // REPLICATION [01.00.04:L50-L57] carries NO ON DELETE clause - the only later mention is a
        // no-op sp_rename at 02.00.00:L154 - so NoAction reproduces the shipped constraint exactly.
        // It is also the only behaviour available: SQL Server cannot create a self-referencing
        // foreign key with ON DELETE CASCADE at all. DECLARING THIS RELATIONSHIP IS MANDATORY
        // RATHER THAN OPTIONAL. Left to convention, Entity Framework would infer it and apply a
        // cascading delete, producing a self-referencing cascade path that is both wrong against
        // this schema and unimplementable on this database. The foreign-key column is nullable, so
        // the relationship is optional: a root page has no parent.
        builder.HasOne(t => t.Parent)
            .WithMany(t => t.Children)
            .HasForeignKey(t => t.ParentId)
            .HasConstraintName("FK_Tabs_Tabs")
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: the terminal index set is exactly two SINGLE-COLUMN, NON-UNIQUE nonclustered
        // indexes, created at 03.00.09:L349-L350 after the earlier definitions from 01.00.10:L811
        // and L819 were dropped at 03.00.09:L343 and L345. Naming them here keeps the physical
        // names the database actually carries - the legacy object qualifier is empty, so no prefix
        // applies - rather than accepting the convention-generated names.
        //
        // MIGRATION: THERE IS NO UNIQUE CONSTRAINT OVER (PortalID, TabName) IN THE TERMINAL SCHEMA,
        // and this file deliberately declares none. One existed historically - IX_Tabs UNIQUE
        // NONCLUSTERED (PortalID, TabName), added at 01.00.08:L6072-L6073 and left untouched by the
        // no-op sp_rename at 02.00.00:L125 - but it was DROPPED at 02.00.01:L64-L65 and never
        // recreated, which the uninstall script corroborates by listing only IX_Tabs_1 and
        // IX_Tabs_2 for this table. Duplicate page names within a portal are therefore legal at the
        // storage layer. Marking that pair unique here would fabricate a constraint the database
        // does not have and would reject data a real installation can legitimately hold; rejecting
        // duplicates, if wanted at all, is an Application-layer rule. This correction is recorded
        // rather than silently absorbed, because the requirement text this file was written from
        // asserted the constraint still existed.
        builder.HasIndex(t => t.PortalId)
            .HasDatabaseName("IX_Tabs_1");

        builder.HasIndex(t => t.ParentId)
            .HasDatabaseName("IX_Tabs_2");
    }
}
