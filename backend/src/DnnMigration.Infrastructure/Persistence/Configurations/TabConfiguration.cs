using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Tab"/> aggregate to the existing, immutable DotNetNuke 4.9 table <c>dbo.Tabs</c>:
/// the page abstraction that module placement, page permissions and portal navigation are all keyed by.
/// </summary>
/// <remarks>
/// <para>
/// Every column name, store type, length, nullability and default below is taken from the <em>terminal</em>
/// state of the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, never from the baseline creation script alone.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything: the
/// mapping describes a table that already exists so that Entity Framework can address it, and the baseline
/// migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// </remarks>
internal sealed class TabConfiguration : IEntityTypeConfiguration<Tab>
{
    /// <summary>Applies the mapping for the tab entity type.</summary>
    /// <param name="builder">The builder for the tab entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Tab> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // objectQualifier is empty and databaseOwner is "dbo" in the legacy provider registration
        // (Website/release.config:L354-L355), so the table is unprefixed and lives in dbo.
        builder.ToTable("Tabs", "dbo");

        // PK_Tabs PRIMARY KEY NONCLUSTERED (TabID), declared alongside DF_Tabs_IsVisible at
        // 01.00.00:L496-L502. The chain never drops or rebuilds it.
        builder.HasKey(t => t.TabId).HasName("PK_Tabs").IsClustered(false);

        // TabID is IDENTITY(0, 1) [01.00.00:L140], so the FIRST REAL TAB IS IDENTIFIED BY ZERO. Zero is a
        // legitimate, persisted page identifier and must never be read as "absent", "unset" or "not yet
        // saved", despite the legacy Null.NullInteger idiom that reserved -1 for absence.
        builder.Property(t => t.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // int NOT NULL [01.00.00:L141], tightened at 03.01.01:L1278 with DF_Tabs_TabOrder DEFAULT (0) at
        // 01.00.05:L982-L983 and re-added at 03.01.01:L1284. The store default equals the CLR default, so
        // letting the database supply it cannot change a caller's value.
        builder.Property(t => t.TabOrder)
            .HasColumnName("TabOrder")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(t => t.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // The only non-nullable string on the table: nvarchar(50) NOT NULL [01.00.00:L143].
        builder.Property(t => t.TabName)
            .HasColumnName("TabName")
            .HasMaxLength(50)
            .IsRequired();

        // Bit NOT NULL [01.00.00:L149], tightened at 03.01.01:L1279, and the ONLY bit on this table whose
        // store default is true - DF_Tabs_IsVisible DEFAULT (1) at 01.00.00:L497, re-added at
        // 03.01.01:L1286.
        builder.Property(t => t.IsVisible)
            .HasColumnName("IsVisible")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // int NULL [01.00.04:L46-L47] - the column carries a lower-case d, which the terminal index
        // definition at 03.00.09:L350 confirms. Nullable and deliberately not required: a null is a
        // root-level page.
        builder.Property(t => t.ParentId)
            .HasColumnName("ParentId")
            .HasColumnType("int");

        // int NOT NULL CONSTRAINT DF_Tabs_Level DEFAULT 0 [01.00.05:L977-L978], tightened at 03.01.01:L1280
        // with the default re-added at L1288.
        builder.Property(t => t.Level)
            .HasColumnName("Level")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // nvarchar(100) NULL [01.00.05:L979].
        builder.Property(t => t.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // bit, added NOT NULL DEFAULT (0) at 01.00.10:L277.
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

        // The member is spelled Keywords with a lower-case w while the column it binds to is spelled
        // KeyWords with a capital W [02.00.00:L4754]. This explicit HasColumnName is the single place that
        // difference is reconciled.
        builder.Property(t => t.Keywords)
            .HasColumnName("KeyWords")
            .HasMaxLength(500);

        // bit NOT NULL CONSTRAINT DF_Tabs_IsDeleted DEFAULT 0 [02.00.00:L4755], tightened at 03.01.01:L1282
        // with the default re-added at L1292. A soft delete: the row survives and read paths that present
        // live pages filter on this flag.
        builder.Property(t => t.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // nvarchar(255) NULL [02.02.00:L39-L40]. The column is spelled Url, not URL; the terminal view
        // aliases the same column as URL at 04.05.04:L82, which is the identical column under a
        // case-insensitive identifier.
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

        // bit NOT NULL CONSTRAINT DF_Tabs_IsSecure DEFAULT (0) [04.05.04:L45-L46]. This is the last column
        // the chain ever adds to this table, in the terminal script, and the terminal view selects it at
        // L86.
        builder.Property(t => t.IsSecure)
            .HasColumnName("IsSecure")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // Six columns present earlier in the chain are absent from the terminal schema and are deliberately
        // unmapped: LeftPaneWidth [dropped 02.00.00:L4735-L4736], RightPaneWidth [:L4739-L4740],
        // MobileTabName [:L4743-L4744], ShowMobile [:L4747-L4748], AdministratorRoles [added
        // 01.00.06:L593-L594, dropped 03.00.01:L1407-L1408] and AuthorizedRoles [dropped
        // 03.00.01:L1410-L1411].

        // FK_Tabs_Portals FOREIGN KEY (PortalID) REFERENCES dbo.Portals (PortalID) ON DELETE CASCADE NOT
        // FOR REPLICATION - created at 01.00.00:L607-L614, dropped at 01.00.05:L1418 L1419 and re-added
        // with the same cascade at 01.00.05:L1540-L1547.
        builder.HasOne(t => t.Portal)
            .WithMany(p => p.Tabs)
            .HasForeignKey(t => t.PortalId)
            .HasConstraintName("FK_Tabs_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // FK_Tabs_Tabs FOREIGN KEY (ParentId) REFERENCES dbo.Tabs (TabID) NOT FOR REPLICATION
        // [01.00.04:L50-L57] carries NO ON DELETE clause - the only later mention is a no-op sp_rename at
        // 02.00.00:L154 - so NoAction reproduces the shipped constraint exactly.
        builder.HasOne(t => t.Parent)
            .WithMany(t => t.Children)
            .HasForeignKey(t => t.ParentId)
            .HasConstraintName("FK_Tabs_Tabs")
            .OnDelete(DeleteBehavior.NoAction);

        // The terminal index set is exactly two SINGLE-COLUMN, NON-UNIQUE nonclustered indexes, created at
        // 03.00.09:L349-L350 after the earlier definitions from 01.00.10:L811 and L819 were dropped at
        // 03.00.09:L343 and L345.
        builder.HasIndex(t => t.PortalId)
            .HasDatabaseName("IX_Tabs_1");

        builder.HasIndex(t => t.ParentId)
            .HasDatabaseName("IX_Tabs_2");
    }
}
