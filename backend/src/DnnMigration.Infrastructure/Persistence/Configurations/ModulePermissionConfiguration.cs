using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="ModulePermission"/> to the existing, immutable DotNetNuke 4.9 SQL Server table
/// <c>dbo.ModulePermission</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table name is singular.</b> The terminal schema declares <c>ModulePermission</c>, so the mapping
/// states the name explicitly and the provider's pluralising convention is deliberately overridden.
/// <c>ModulePermissions</c> does not exist in any database this model is pointed at, and the mistake would
/// surface only as a runtime failure on first query.
/// </para>
/// <para>
/// <b>A negative role identifier is real data, not an absent value.</b> <c>-1</c>, <c>-2</c> and <c>-3</c>
/// are persisted pseudo-principals that match no <c>dbo.Roles</c> row. Absence is expressed by SQL
/// <c>NULL</c> and by nothing else, so no value conversion between the two ever appears here.
/// </para>
/// </remarks>
internal sealed class ModulePermissionConfiguration : IEntityTypeConfiguration<ModulePermission>
{
    /// <summary>Applies the mapping for the module grant entity type.</summary>
    /// <param name="builder">The builder for the module grant entity type.</param>
    public void Configure(EntityTypeBuilder<ModulePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // How the citations below were obtained, recorded so they can be re-checked rather than trusted.

        // SINGULAR legacy table name. CREATE TABLE ModulePermission is declared once in the whole chain, at
        // 02.02.00:L675, and the provider's pluralising convention would look for a ModulePermissions table
        // that does not exist.
        builder.ToTable("ModulePermission", "dbo");

        // Primary-key lineage. Added as PK_ModulePermission PRIMARY KEY CLUSTERED over ModulePermissionID
        // at 02.02.00:L716-L720, dropped at 03.00.09:L461 and recreated clustered at 03.00.09:L477-L478 -
        // the terminal form.
        builder.HasKey(g => g.ModulePermissionId)
            .HasName("PK_ModulePermission");

        // ModulePermissionID int IDENTITY (1, 1) NOT NULL at 02.02.00:L676. The column keeps the legacy
        // upper-case ID spelling.
        builder.Property(g => g.ModulePermissionId)
            .HasColumnName("ModulePermissionID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // ModuleID int NOT NULL at 02.02.00:L677, never re-typed by any later script. Required, and backed
        // by a real cascading foreign key configured below.
        builder.Property(g => g.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .IsRequired();

        // PermissionID int NOT NULL at 02.02.00:L678, never re-typed. Required, and backed by a real
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

        // UserID did not exist in the original table. 04.05.00:L639-L642 adds it as int NULL, guarded by IF
        // (SELECT COLUMNPROPERTY(OBJECT_ID('ModulePermission'), 'UserID', 'AllowsNull')) IS Null so the
        // script is re-runnable - which makes the column look conditional in the script while being
        // unconditionally present in any 4.9 database.
        builder.Property(g => g.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int");

        // AllowAccess bit NOT NULL at 02.02.00:L680, declared WITHOUT a database default unlike several bit
        // columns on Modules, Tabs and TabModules, which is why no default value appears anywhere in this
        // file. Required.
        builder.Property(g => g.AllowAccess)
            .HasColumnName("AllowAccess")
            .HasColumnType("bit")
            .IsRequired();

        // This file declares FOUR relationships and it declares every one of them from the DEPENDENT end.

        // FK_ModulePermission_Modules FOREIGN KEY (ModuleID) REFERENCES Modules (ModuleID) ON DELETE
        // CASCADE. Declared at 02.02.00:L761-L767, dropped at 03.00.09:L450-L451 and re-added at
        // 03.00.09:L491-L493 - the terminal form.
        builder.HasOne(g => g.Module)
            .WithMany(m => m.ModulePermissions)
            .HasForeignKey(g => g.ModuleId)
            .HasConstraintName("FK_ModulePermission_Modules")
            .OnDelete(DeleteBehavior.Cascade);

        // FK_ModulePermission_Permission FOREIGN KEY (PermissionID) REFERENCES Permission (PermissionID) ON
        // DELETE CASCADE. Declared at 02.02.00:L768-L773, dropped at 03.00.09:L447-L448 and re-added at
        // 03.00.09:L491-L492 - the terminal form.
        builder.HasOne(g => g.Permission)
            .WithMany(p => p.ModulePermissions)
            .HasForeignKey(g => g.PermissionId)
            .HasConstraintName("FK_ModulePermission_Permission")
            .OnDelete(DeleteBehavior.Cascade);

        // FOREIGN-KEY ABSENCE PROOF. There is NO physical foreign key from this table to Roles anywhere in
        // the terminal schema, established two independent ways over all 87 non-UnInstall scripts using the
        // normalising search described at the top of this method.
        builder.HasOne(g => g.Role)
            .WithMany(r => r.ModulePermissions)
            .HasForeignKey(g => g.RoleId)
            .OnDelete(DeleteBehavior.NoAction);

        // The account key EXISTS, and the only reason anyone would conclude otherwise is its name. It is
        // FK_ModulePermissionUsers - with NO underscore before "Users" - declared at 04.05.00:L644-L651,
        // whereas its page-level twin is FK_TabPermission_Users with one at 04.05.00:L486.
        builder.HasOne(g => g.User)
            .WithMany(u => u.ModulePermissions)
            .HasForeignKey(g => g.UserId)
            .HasConstraintName("FK_ModulePermissionUsers")
            .OnDelete(DeleteBehavior.NoAction);

        // The terminal object set for this table is exactly ten objects, and every one is accounted for in
        // this file.
        builder.HasIndex(g => new { g.ModuleId, g.PermissionId, g.RoleId, g.UserId })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_ModulePermission");

        // Four single-column lookup indexes, one per foreign-key column plus one for the unenforced role
        // column, added at 04.06.00:L1207-L1230 under IF NOT EXISTS (SELECT * FROM dbo.sysindexes WHERE
        // name = N'...') guards. All four are NON-unique; only the four-column constraint above is unique.
        builder.HasIndex(g => g.PermissionId)
            .HasDatabaseName("IX_ModulePermission_Permission");

        builder.HasIndex(g => g.ModuleId)
            .HasDatabaseName("IX_ModulePermission_Modules");

        builder.HasIndex(g => g.UserId)
            .HasDatabaseName("IX_ModulePermission_Users");

        builder.HasIndex(g => g.RoleId)
            .HasDatabaseName("IX_ModulePermission_Roles");
    }
}
