using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="TabPermission"/> to the existing, immutable DotNetNuke 4.9 SQL Server table
/// <c>dbo.TabPermission</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table name is singular.</b> The terminal schema declares <c>TabPermission</c>, so the name is
/// stated explicitly and the provider's pluralising convention is deliberately overridden. The plural
/// spelling exists in this database only as the name of a legacy read view, never as a table, and the
/// mistake would surface solely as a runtime failure on the first query rather than at build time.
/// </para>
/// <para>
/// <b>A negative role identifier is real data, not an absent value.</b> <c>-1</c>, <c>-2</c> and <c>-3</c>
/// are persisted pseudo-principals that match no <c>dbo.Roles</c> row. Absence is expressed by SQL
/// <c>NULL</c> and by nothing else, so no value conversion between the two ever appears here.
/// </para>
/// </remarks>
internal sealed class TabPermissionConfiguration : IEntityTypeConfiguration<TabPermission>
{
    /// <summary>Applies the mapping for the page grant entity type.</summary>
    /// <param name="builder">The builder for the page grant entity type.</param>
    public void Configure(EntityTypeBuilder<TabPermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // How the citations below were obtained, recorded so they can be re-checked rather than trusted.

        // SINGULAR legacy table name. CREATE TABLE TabPermission is declared once in the whole chain, at
        // 02.02.00:L693, and the provider's pluralising convention would look for a plural table that does
        // not exist.
        builder.ToTable("TabPermission", "dbo");

        // Primary-key lineage. Added as PK_TabPermission PRIMARY KEY CLUSTERED over TabPermissionID at
        // 02.02.00:L730-L734, dropped at 03.00.09:L465 and recreated clustered at 03.00.09:L473 - the
        // terminal form.
        builder.HasKey(g => g.TabPermissionId)
            .HasName("PK_TabPermission");

        // TabPermissionID int IDENTITY (1, 1) NOT NULL at 02.02.00:L694. The column keeps the legacy
        // upper-case ID spelling.
        builder.Property(g => g.TabPermissionId)
            .HasColumnName("TabPermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // TabID int NOT NULL at 02.02.00:L695, never re-typed by any later script. Required, and backed by
        // a real cascading foreign key configured below.
        builder.Property(g => g.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .IsRequired();

        // PermissionID int NOT NULL at 02.02.00:L696, never re-typed. Required, and backed by a real
        // cascading foreign key configured below.
        builder.Property(g => g.PermissionId)
            .HasColumnName("PermissionID")
            .HasColumnType("int")
            .IsRequired();

        // RoleID is NULLABLE in the terminal schema, and the provenance matters because the baseline
        // declaration says otherwise.
        builder.Property(g => g.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int");

        // UserID did not exist in the original table. 04.05.00:L482-L483 adds it as int NULL, guarded by IF
        // (SELECT COLUMNPROPERTY(OBJECT_ID('TabPermission'), 'UserID', 'AllowsNull')) IS Null at L480 so
        // the script is re-runnable - which makes the column look conditional in the script while being
        // unconditionally present in any 4.9 database.
        builder.Property(g => g.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int");

        // AllowAccess bit NOT NULL at 02.02.00:L698, declared WITHOUT a database default unlike several bit
        // columns on Modules, Tabs and TabModules - which is why no default value appears anywhere in this
        // file. Required.
        builder.Property(g => g.AllowAccess)
            .HasColumnName("AllowAccess")
            .HasColumnType("bit")
            .IsRequired();

        // This file declares FOUR relationships and it declares every one of them from the DEPENDENT end.

        // FK_TabPermission_Tabs FOREIGN KEY (TabID) REFERENCES Tabs (TabID) ON DELETE CASCADE. Declared at
        // 02.02.00:L783-L788, dropped at 03.00.09:L456-L457 and re-added at 03.00.09:L487 and L489 - the
        // terminal form, added in the same single statement as the catalogue key below.
        builder.HasOne(g => g.Tab)
            .WithMany(t => t.TabPermissions)
            .HasForeignKey(g => g.TabId)
            .HasConstraintName("FK_TabPermission_Tabs")
            .OnDelete(DeleteBehavior.Cascade);

        // FK_TabPermission_Permission FOREIGN KEY (PermissionID) REFERENCES Permission (PermissionID) ON
        // DELETE CASCADE. Declared at 02.02.00:L777-L782, dropped at 03.00.09:L453-L454 and re-added at
        // 03.00.09:L487-L488 - the terminal form.
        builder.HasOne(g => g.Permission)
            .WithMany(p => p.TabPermissions)
            .HasForeignKey(g => g.PermissionId)
            .HasConstraintName("FK_TabPermission_Permission")
            .OnDelete(DeleteBehavior.Cascade);

        // FOREIGN-KEY ABSENCE PROOF. There is NO physical foreign key from this table to Roles anywhere in
        // the terminal schema, established two independent ways over all 87 non-UnInstall scripts using the
        // normalising search described at the top of this method.
        builder.HasOne(g => g.Role)
            .WithMany(r => r.TabPermissions)
            .HasForeignKey(g => g.RoleId)
            .OnDelete(DeleteBehavior.NoAction);

        // The account key on THIS table is the conventionally named one, and that is itself the finding. It
        // is FK_TabPermission_Users - WITH the separator - declared at 04.05.00:L485-L492, whereas the
        // module grant twin is FK_ModulePermissionUsers with none at 04.05.00:L645.
        builder.HasOne(g => g.User)
            .WithMany(u => u.TabPermissions)
            .HasForeignKey(g => g.UserId)
            .HasConstraintName("FK_TabPermission_Users")
            .OnDelete(DeleteBehavior.NoAction);

        // The terminal object set for this table is exactly ten objects, and every one is accounted for in
        // this file.
        builder.HasIndex(g => new { g.TabId, g.PermissionId, g.RoleId, g.UserId })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_TabPermission");

        // Four single-column lookup indexes, one per foreign-key column plus one for the unenforced role
        // column, added at 04.06.00:L1181-L1203 under IF NOT EXISTS (SELECT * FROM dbo.sysindexes WHERE
        // name = N'...') guards, beneath the comment at L1180.
        builder.HasIndex(g => g.PermissionId)
            .HasDatabaseName("IX_TabPermission_Permission");

        builder.HasIndex(g => g.TabId)
            .HasDatabaseName("IX_TabPermission_Tabs");

        builder.HasIndex(g => g.UserId)
            .HasDatabaseName("IX_TabPermission_Users");

        builder.HasIndex(g => g.RoleId)
            .HasDatabaseName("IX_TabPermission_Roles");
    }
}
