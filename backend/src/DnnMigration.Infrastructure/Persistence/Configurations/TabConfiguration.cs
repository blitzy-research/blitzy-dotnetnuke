using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Tab"/> aggregate to the legacy <c>dbo.Tabs</c> table.
/// </summary>
/// <remarks>
/// <para>
/// A tab is the page abstraction the whole navigation and permission model is keyed by, so the
/// mapping has to be exact. Six columns present in the baseline table are gone from the terminal
/// one: <c>MobileTabName</c>, <c>ShowMobile</c>, <c>LeftPaneWidth</c> and <c>RightPaneWidth</c> were
/// dropped at 02.00.00, and <c>AdministratorRoles</c> and <c>AuthorizedRoles</c> at 03.00.01 when
/// the semicolon-delimited role strings were replaced by rows in <c>TabPermission</c>. None of them
/// is mapped.
/// </para>
/// <para>
/// <c>Level</c> is a keyword-adjacent identifier and the schema brackets it. Entity Framework quotes
/// every identifier it emits, so naming the column plainly here is safe and produces
/// <c>[Level]</c> in the generated SQL.
/// </para>
/// <para>
/// There is no unique index over the tenant and tab name at the terminal state. One was added at
/// 01.00.08 and dropped again at 02.00.01, and the uninstall script confirms it is absent: it lists
/// only the primary key and the five default constraints. Duplicate tab names are therefore legal at
/// the storage layer, and the tab service — not this mapping — decides whether to reject them.
/// </para>
/// <para>
/// The store default for <c>IsVisible</c> is <c>1</c>, which differs from the CLR default of
/// <see langword="false"/>. It is deliberately <em>not</em> configured. Entity Framework omits a
/// column from an INSERT when the property still holds the CLR default and a default is configured,
/// so declaring it would turn a request for a hidden page into a visible one. The entity carries the
/// same default in its own initialiser instead, which cannot be bypassed that way. The five
/// zero-valued defaults below are safe by the same test and are declared.
/// </para>
/// <para>
/// The parent reference is a self-relationship whose foreign key carries no <c>ON DELETE</c> clause,
/// so it is mapped with no delete behaviour: removing a parent page must not silently reparent or
/// delete its children behind the service layer's back.
/// </para>
/// </remarks>
internal sealed class TabConfiguration : IEntityTypeConfiguration<Tab>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the tab entity type.</param>
    public void Configure(EntityTypeBuilder<Tab> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Tabs", "dbo");

        builder.HasKey(t => t.TabId).HasName("PK_Tabs");

        builder.Property(t => t.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        builder.Property(t => t.TabOrder)
            .HasColumnName("TabOrder")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(t => t.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        builder.Property(t => t.TabName)
            .HasColumnName("TabName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(t => t.IsVisible)
            .HasColumnName("IsVisible")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(t => t.ParentId)
            .HasColumnName("ParentId")
            .HasColumnType("int");

        builder.Property(t => t.Level)
            .HasColumnName("Level")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(t => t.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        builder.Property(t => t.DisableLink)
            .HasColumnName("DisableLink")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(t => t.Title)
            .HasColumnName("Title")
            .HasMaxLength(200);

        builder.Property(t => t.Description)
            .HasColumnName("Description")
            .HasMaxLength(500);

        // The member is spelled Keywords and the column is spelled KeyWords. This explicit
        // HasColumnName is the single place that difference is reconciled: property modernisation
        // must never rename a column, and no other layer reproduces the bridge.
        builder.Property(t => t.Keywords)
            .HasColumnName("KeyWords")
            .HasMaxLength(500);

        builder.Property(t => t.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(t => t.Url)
            .HasColumnName("Url")
            .HasMaxLength(255);

        builder.Property(t => t.SkinSrc)
            .HasColumnName("SkinSrc")
            .HasMaxLength(200);

        builder.Property(t => t.ContainerSrc)
            .HasColumnName("ContainerSrc")
            .HasMaxLength(200);

        builder.Property(t => t.TabPath)
            .HasColumnName("TabPath")
            .HasMaxLength(255);

        builder.Property(t => t.StartDate)
            .HasColumnName("StartDate")
            .HasColumnType("datetime");

        builder.Property(t => t.EndDate)
            .HasColumnName("EndDate")
            .HasColumnType("datetime");

        builder.Property(t => t.RefreshInterval)
            .HasColumnName("RefreshInterval")
            .HasColumnType("int");

        builder.Property(t => t.PageHeadText)
            .HasColumnName("PageHeadText")
            .HasMaxLength(500);

        builder.Property(t => t.IsSecure)
            .HasColumnName("IsSecure")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasOne(t => t.Portal)
            .WithMany(p => p.Tabs)
            .HasForeignKey(t => t.PortalId)
            .HasConstraintName("FK_Tabs_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Parent)
            .WithMany(t => t.Children)
            .HasForeignKey(t => t.ParentId)
            .HasConstraintName("FK_Tabs_Tabs")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
