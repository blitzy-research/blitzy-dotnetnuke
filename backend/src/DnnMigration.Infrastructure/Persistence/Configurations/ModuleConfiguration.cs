using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds <see cref="Module"/> to the existing, unaltered DotNetNuke 4.9 SQL Server table
/// <c>dbo.Modules</c> through the Entity Framework Core Fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is immutable.</b> Nothing here creates, alters, drops or seeds a database object.
/// Every column is bound by an explicit name, and the key, the index and both foreign keys carry the
/// physical names the installation already has, so renaming a member of the model can never silently
/// rename anything in the database.
/// </para>
/// <para>
/// <b>The mapping targets the terminal schema</b> - the cumulative result of replaying the upgrade
/// scripts under <c>Website/Providers/DataProviders/SqlDataProvider/</c> in version order. For this
/// table the distinction is decisive rather than pedantic, because it is the most heavily rewritten
/// table in the in-scope schema. Ten scripts change it - <c>01.00.00</c>, <c>01.00.04</c>,
/// <c>01.00.08</c>, <c>01.00.10</c>, <c>02.00.00</c>, <c>02.02.00</c>, <c>02.02.02</c>,
/// <c>03.00.01</c>, <c>03.00.09</c> and <c>03.01.01</c> - and nothing after <c>03.01.01</c> touches
/// it, so the replay terminates there. Fifteen columns those scripts declared are gone, one foreign
/// key was removed entirely, and one index was dropped and never recreated. Eleven columns survive,
/// and this configuration maps those eleven and nothing else.
/// </para>
/// <para>
/// <b>Eleven is corroborated independently of the schema replay.</b> The legacy data surface agrees
/// exactly: <c>Library/Components/Providers/Data/DataProvider.vb</c> line 132 declares
/// <c>AddModule</c> with ten parameters, one for every column the database does not generate, and
/// line 133 declares <c>UpdateModule</c> with that same list less the tenant key, which insertion
/// fixes and no update rewrites. The legacy provider registration uses an empty object qualifier and
/// <c>dbo</c> as the database owner (<c>Website/release.config</c> lines 354-355), so the templated
/// script names resolve unqualified in <c>dbo</c>; an installation carrying a different qualifier is
/// a configuration difference, resolved where the context is composed rather than here.
/// </para>
/// <para>
/// <b>Two relationships are declared here, exactly two, and their delete behaviours differ.</b> This
/// entity carries both foreign key columns, so it is the dependent half of both relationships and
/// therefore owns both. Its own inverse navigations are owned by the dependents that carry the
/// matching key columns - <c>ModuleSettingConfiguration</c>, <c>TabModuleConfiguration</c> and
/// <c>ModulePermissionConfiguration</c> - and neither <c>ModuleDefinitionConfiguration</c> nor
/// <c>PortalConfiguration</c> declares anything for the two edges below. Declaring a relationship
/// from both ends is neither a compile error nor a model-validation error, and that is precisely the
/// hazard: the assembly scan that applies these configurations guarantees no ordering, so whichever
/// end ran last would silently decide the delete behaviour and could soften a cascade the scripts
/// record or invent one they do not.
/// </para>
/// </remarks>
internal sealed class ModuleConfiguration : IEntityTypeConfiguration<Module>
{
    /// <summary>
    /// Applies the terminal-schema mapping for <see cref="Module"/>.
    /// </summary>
    /// <param name="builder">
    /// The builder supplied by the model builder when the containing assembly is scanned for
    /// configurations. This type declares no constructor on purpose: the scan accepts only types
    /// exposing a public parameterless constructor, so declaring a non-public one would make the
    /// configuration undiscoverable with no diagnostic of any kind, and the conventions that then
    /// took over would look for columns this table does not have.
    /// </param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Module> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy module model was one flattened object. ModuleInfo.vb presented
        //   roughly fifty-eight properties over the join of Modules, TabModules, ModuleDefinitions,
        //   DesktopModules and ModuleControls, and implemented IPropertyAccess besides
        //   (Library/Components/Modules/ModuleInfo.vb lines 36-37) to serve the token-replacement
        //   subsystem this migration excludes. A caller could not tell which physical row a value
        //   belonged to, and every read paid for the whole join. The target splits that object along
        //   the PHYSICAL TABLE BOUNDARIES, and this configuration maps only the columns of this one
        //   table. The same note appears in ModuleDefinitionConfiguration,
        //   ModuleControlConfiguration, DesktopModuleConfiguration and TabModuleConfiguration.
        builder.ToTable("Modules", "dbo");

        // 01.00.00:L514-518 - ALTER TABLE dbo.Modules WITH NOCHECK ADD CONSTRAINT PK_Modules PRIMARY
        // KEY NONCLUSTERED (ModuleID), the constraint name at line 515. Named explicitly so the
        // model carries the name the database already has rather than one invented by convention.
        // NONCLUSTERED is a physical storage choice with no counterpart in the model and is left
        // unrepresented, here and across this folder.
        builder.HasKey(m => m.ModuleId)
            .HasName("PK_Modules");

        // 01.00.00:L221 - ModuleID int IDENTITY (0, 1) NOT NULL. The seed is recorded so that a
        // model-generated script would continue the existing sequence rather than restart it.
        //
        // MIGRATION: the seed of ZERO means the first module of a fresh installation is identified
        //   by 0, so zero is a legitimate, persisted module identifier. It must never be read as
        //   absent, unset, transient or defaulted, and no `== 0`, `<= 0` or `default(int)` heuristic
        //   may be written against it anywhere in this solution. The legacy model had no way to
        //   express that distinction: Library/Components/Shared/Null.vb declares its absent-integer
        //   marker as -1, so absence and a real key were the same kind of value. In the target,
        //   absence is expressed by a nullable CLR type and by nothing else, and whether a row
        //   exists is answered by the change tracker.
        builder.Property(m => m.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // MIGRATION: the legacy read path hydrated this entity by hand. The private hydrator in
        //   Library/Components/Modules/ModuleController.vb constructs the object at line 54 and then
        //   assigns one property per line across lines 66-79, each assignment wrapping the raw
        //   reader value in a Convert call over the sentinel helper declared in
        //   Library/Components/Shared/Null.vb, so a database null became -1, an empty string or the
        //   earliest representable date before it ever reached the object. The Entity Framework Core
        //   materialiser replaces that loop in its entirety. No sentinel translation of any kind
        //   happens in this layer: a database null materialises as a CLR null, which is why every
        //   nullable column below carries no default and no value converter.

        // MIGRATION: fifteen columns this table once carried are absent from the terminal schema and
        //   are deliberately left unmapped; binding any of them would fail at run time on an invalid
        //   column name. Two went at 02.00.00:L6559-6560 and L6563-6564. Ten more went in a single
        //   statement at 03.00.01:L197-198, and the page key went at 03.00.01:L204-205 - those
        //   eleven are the PLACEMENT facts, and 03.00.01:L19-33 had just created dbo.TabModules to
        //   hold them, so they are mapped by TabModuleConfiguration and belong nowhere else. The last
        //   two, at 03.00.01:L1401-1402 and L1404-1405, were role-name strings; a grant is a row in
        //   dbo.ModulePermission now, reached through the ModulePermissions navigation. Four of the
        //   fifteen were themselves later additions, so their whole lifetime sits inside this
        //   history: two at 01.00.04:L86-87, one at 01.00.08:L6144-6145 and one at
        //   02.02.02:L2536-2537.

        // MIGRATION: the property is spelled ModuleDefinitionId in full, but the column keeps the
        //   abbreviated legacy spelling ModuleDefID (01.00.00:L223). That abbreviation is not local
        //   to this table - it is the spelling every table referencing a definition uses, in
        //   ModuleDefinitions, ModuleControls, Permission and ProfilePropertyDefinition alike - so
        //   the explicit column name below is the single thing standing between the model and a query
        //   against a column that does not exist.
        builder.Property(m => m.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .IsRequired();

        // 03.00.01:L11-12 - PortalID int NULL. It stays nullable to the end of the chain; no later
        // script makes it required.
        //
        // MIGRATION: null is MEANINGFUL here rather than unknown - a module with no tenant is owned
        //   by the installation itself - and it is modelled as a nullable integer rather than as a
        //   required integer carrying the legacy absent-integer marker of -1. That marker would
        //   collide with real data: dbo.Portals.PortalID is declared IDENTITY(-1, 1), so -1 is a
        //   genuine tenant key and 0 is the second one. Neither value may be read as an absence on
        //   this column.
        builder.Property(m => m.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // 01.00.00:L226 - ModuleTitle nvarchar (256) NULL. The only variable-width text column on the
        // table and so the only one carrying a length. Unicode is the default for a string property
        // and is not restated; the table declares no ANSI text column that would need that default
        // overridden. Nullable, so an untitled module and a module titled with an empty string stay
        // distinct states, which they were not in the legacy model.
        builder.Property(m => m.ModuleTitle)
            .HasColumnName("ModuleTitle")
            .HasMaxLength(256);

        // 01.00.04:L84-85 adds AllTabs bit NOT NULL CONSTRAINT DF_Modules_AllTabs DEFAULT 0, and
        // 03.01.01 restates both flags on this table: L1027 re-asserts this column as bit NOT NULL
        // and L1030 re-creates its default at 0. The default therefore survives to the terminal
        // schema and is reproduced, so an insert that omits the column lands on false exactly as the
        // database would decide rather than failing.
        builder.Property(m => m.AllTabs)
            .HasColumnName("AllTabs")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // 02.00.00:L6567-6568 adds IsDeleted bit NOT NULL CONSTRAINT DF_Modules_IsDeleted DEFAULT 0,
        // restated by 03.01.01:L1028 with its default re-created at L1032. This is the soft-delete
        // flag behind the recycle bin: a removed module keeps its row, so this mapping declares no
        // global filter and each read path decides for itself whether removed rows belong in its
        // answer.
        builder.Property(m => m.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // 02.02.00:L44-45 - InheritViewPermissions bit NULL. No later script makes it NOT NULL, so
        // nullable is the terminal shape.
        //
        // MIGRATION: this column is a TRI-STATE and its third state carries meaning, so it is mapped
        //   with NO required declaration and NO default: true defers view access to the hosting page,
        //   false applies the module's own grants, and null is a row the flag was never written on.
        //   The legacy stack could not represent that at all - ModuleInfo.vb line 347 declared a
        //   plain VB Boolean, and SqlDataProvider.vb line 706 passes this value straight through
        //   while wrapping every other nullable column of this table in its null helper - so every
        //   unwritten row arrived as false and "never set" merged silently with "set to false".
        //   Collapsing it back to a non-nullable flag would change permission evaluation.
        builder.Property(m => m.InheritViewPermissions)
            .HasColumnName("InheritViewPermissions")
            .HasColumnType("bit");

        // 02.02.00:L321-325 adds four columns in one statement: Header ntext NULL at line 322, Footer
        // ntext NULL at 323, StartDate datetime NULL at 324 and EndDate datetime NULL at 325.
        //
        // MIGRATION: the two markup columns keep the legacy large-object type verbatim, because the
        //   schema is immutable under this migration and widening the column to a modern large-object
        //   type would be a schema change this work does not make. The store type is stated
        //   explicitly for that reason, and NO maximum length is declared for either: a length on
        //   that type is meaningless and would make the provider emit a different type altogether.
        //   Both hold untrusted markup, stored and returned as written; encoding it is the
        //   responsibility of whatever renders it, never of persistence.
        builder.Property(m => m.Header)
            .HasColumnName("Header")
            .HasColumnType("ntext");

        builder.Property(m => m.Footer)
            .HasColumnName("Footer")
            .HasColumnType("ntext");

        // MIGRATION: both date-boundary columns are the legacy fixed-length date type, never its
        //   modern successor. The store type is stated explicitly for that reason: the provider's
        //   default for a date property is the modern type, whose range and precision differ from
        //   what these columns hold, and the schema is immutable. Null means "no boundary" - the
        //   legacy constructor seeded these fields with the earliest representable date, which made
        //   an absent boundary indistinguishable from a real one.
        builder.Property(m => m.StartDate)
            .HasColumnName("StartDate")
            .HasColumnType("datetime");

        builder.Property(m => m.EndDate)
            .HasColumnName("EndDate")
            .HasColumnType("datetime");

        // Index lifecycle, read from the whole chain rather than from any single script:
        // 01.00.10:L871-877 drops any earlier index of this name and creates IX_Modules over
        // ModuleDefID; 01.00.10:L879-884 creates a second index over the page key; 03.00.01:L201
        // drops that second index, which is never recreated, because the column it covered was itself
        // dropped three lines later; 03.00.09:L291 drops IX_Modules and L293 recreates it as CREATE
        // NONCLUSTERED INDEX IX_Modules ON Modules (ModuleDefID). That is the terminal state, and the
        // terminal index set on this table is the primary key plus this one index.
        //
        // Deliberately NOT unique: many modules are created from one definition, which is what a
        // definition is for. Asserting a uniqueness the database does not have would make the change
        // tracker reject rows the database accepts.
        builder.HasIndex(m => m.ModuleDefinitionId)
            .HasDatabaseName("IX_Modules");

        // 01.00.00:L673-679 - CONSTRAINT FK_Modules_ModuleDefinitions FOREIGN KEY (ModuleDefID)
        // REFERENCES dbo.ModuleDefinitions (ModuleDefID) ON DELETE CASCADE NOT FOR REPLICATION. Never
        // dropped: the only later mention is a no-op rename to its own name at 02.00.00:L146. The
        // cascade is described as the database declares it rather than softened - deleting a
        // definition already removes every module created from it. The key column is NOT NULL, so the
        // relationship is required. NOT FOR REPLICATION has no counterpart in the model and is left
        // unrepresented across this folder. The foreign key reuses the column mapped above, so no
        // shadow property appears.
        builder.HasOne(m => m.ModuleDefinition)
            .WithMany(d => d.Modules)
            .HasForeignKey(m => m.ModuleDefinitionId)
            .HasConstraintName("FK_Modules_ModuleDefinitions")
            .OnDelete(DeleteBehavior.Cascade);

        // 03.00.09:L295-296 - ALTER TABLE Modules WITH NOCHECK ADD CONSTRAINT FK_Modules_Portals
        // FOREIGN KEY (PortalID) REFERENCES Portals (PortalID) NOT FOR REPLICATION. That is the
        // terminal form: the constraint was first added at 03.00.01:L208-215, dropped at
        // 03.00.09:L288-289 and re-added in the shape above. WITH NOCHECK records that existing rows
        // were not validated when it was created, and it has no counterpart in the model.
        //
        // MIGRATION: the terminal constraint carries NO ON DELETE clause, in either of its two
        //   declarations, so this mapping takes no delete action. That is deliberately ASYMMETRIC
        //   with the sibling edge above and with every other child of the tenant table in this
        //   schema - the alias, the tenant-package grant, the page, the tenant membership row, the
        //   profile definition, the role group and the role all cascade. This table is the exception
        //   the database intends: removing a tenant must not remove its modules, and inventing a
        //   cascade here would destroy rows the constraint exists to protect. Neither may the
        //   optional-relationship convention be left to act in its place, since that would set the
        //   key to null on tracked children and quietly turn a tenant's module into an
        //   installation-owned one.
        //
        // MIGRATION: this table has no module-to-page relationship at all, and none is declared. The
        //   corresponding constraint was created at 01.00.00:L680-685, also cascading, and DROPPED at
        //   03.00.01:L15-16, immediately before 03.00.01:L19-33 created dbo.TabModules and
        //   03.00.01:L204-205 dropped the key column it had used. Placement is a row in that table
        //   now, and TabModuleConfiguration owns both of its edges.
        builder.HasOne(m => m.Portal)
            .WithMany(p => p.Modules)
            .HasForeignKey(m => m.PortalId)
            .HasConstraintName("FK_Modules_Portals")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
