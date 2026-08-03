using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="TabModuleSetting"/> entity to the existing <c>dbo.TabModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Each row holds one name and one value against a single placement of a module on a page, so the same
/// module placed on two pages can be configured differently. That is what separates this store from
/// <c>dbo.ModuleSettings</c>, whose rows belong to the module instance and are therefore shared by
/// every placement of it. The two stores are not interchangeable and neither is a fallback for the
/// other one.
/// </para>
/// <para>
/// The schema is externally owned and immutable for this migration. Every declaration below describes
/// the terminal state of the upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> as it already stands, and nothing in this
/// assembly creates, alters or removes a database object. Exactly three scripts in that chain speak on
/// this table: 03.00.01 creates it together with both its constraints and one index, 03.00.03 removes
/// that index, and 03.00.09 drops and rebuilds both constraints under object-qualifier-aware names. No
/// script anywhere adds, widens, narrows or removes a column of it, so the shape declared at
/// 03.00.01:L722-727 is the shape bound here. Names are pinned explicitly rather than left to
/// convention, because the legacy spelling <c>TabModuleID</c> is not the one convention produces; the
/// legacy provider registration supplies the naming defaults, with an empty object qualifier at
/// <c>Website/release.config</c> line 354 and <c>dbo</c> as the database owner at line 355.
/// </para>
/// <para>
/// <b>The two settings tables are alike in every respect that bears on this file, and two earlier
/// readings that made them differ were mistaken.</b> The corrections are stated here rather than
/// quietly absorbed. Each table carries a real composite clustered primary key over its parent
/// identifier and the setting name; each lost the same redundant non-clustered index over those two
/// columns; and each declares its value column <c>nvarchar(2000)</c>. The first mistaken reading held
/// that only a non-unique index stood over the module-scoped pair, which the resolved scripts
/// contradict at 02.00.01:L47-52. The second held this value column to be many times the wider of the
/// two, which 01.00.08:L6252-6267 contradicts: that script rebuilds the module-scoped table through a
/// temporary copy declaring <c>SettingValue nvarchar(2000) NOT NULL</c> at line 6256 and renames the
/// copy over the original at line 6267, and every narrower value column later in the chain belongs to
/// the scheduling table this migration excludes. Both mistakes share one cause - the scripts are
/// carriage-return delimited and they interpolate the object qualifier into constraint names, so a
/// plain search for a resolved name finds nothing at all, and an append-and-destroy chain means only
/// its cumulative terminal state. Every citation below therefore names the script that last spoke on
/// the point.
/// </para>
/// <para>
/// The class is internal, sealed and declares no constructor of its own, so the compiler supplies a
/// public parameterless one. The context discovers configurations by scanning this assembly for
/// exactly that shape, and a non-public constructor would leave this file inert with no diagnostic of
/// any kind. For this entity that failure is unrecoverable rather than merely quiet: the type carries
/// no member any convention could treat as a key, so the model would fail to validate at all.
/// </para>
/// </remarks>
internal sealed class TabModuleSettingConfiguration : IEntityTypeConfiguration<TabModuleSetting>
{
    /// <summary>
    /// Applies the mapping for one stored setting of a single module placement.
    /// </summary>
    /// <param name="builder">The builder for the placement setting entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<TabModuleSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy model had no type for these rows at all. They were reachable only as
        //   an untyped name-and-value bag, one bag per placement, returned by
        //   Library/Components/Modules/ModuleController.vb line 1336 and filled by a hand-rolled loop
        //   over reader ORDINALS at lines 1345-1351 which put an empty string in place of a database
        //   null that this column cannot even hold. The bag was declared on the abstract data surface
        //   as six members at Library/Components/Providers/Data/DataProvider.vb lines 149-154, reached
        //   through a provider singleton that reflection instantiated, and served by six stored
        //   procedures whose names were built by concatenating a database owner and an object
        //   qualifier at
        //   Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb lines 753-769 - by way
        //   of a helper type that survives in the repository only as a compiled assembly. Writes went
        //   one name at a time at ModuleController.vb lines 1373-1381, each one followed by a cache
        //   eviction keyed on the placement identifier at line 1384. Nothing in that arrangement
        //   recorded what a name or a value was allowed to be, and a name stored empty read back
        //   identically to a name that was never stored at all. The destination models the row as a
        //   row, keyed as the database already keys it, and lets the Entity Framework Core
        //   materialiser hydrate it, so no reflection-driven hydrator, no reader loop and no marker
        //   translation of any kind survives into this layer.
        builder.ToTable("TabModuleSettings", "dbo");

        // MIGRATION: this composite key mirrors a constraint the database already carries, and that
        //   constraint was rebuilt rather than abandoned. 03.00.01:L730-735 declares
        //   PK_TabModuleSettings PRIMARY KEY CLUSTERED (TabModuleID, SettingName), with the name on
        //   line 731; 03.00.09:L331 drops it; and 03.00.09:L333 immediately re-adds it as
        //   PK_{objectQualifier}TabModuleSettings PRIMARY KEY CLUSTERED ([TabModuleID],
        //   [SettingName]), which qualifies the constraint name and leaves the key shape untouched.
        //   That re-add is the terminal statement on the point, and the empty object qualifier
        //   resolves the name to exactly PK_TabModuleSettings. The column order is the database's own
        //   rather than a choice made here - the placement identifier leads and the setting name
        //   follows - and the name is pinned so the model carries the name the database has instead of
        //   one convention would invent. CLUSTERED is a physical storage choice with no counterpart in
        //   the model and is left unrepresented, here and across this folder. This declaration records
        //   an existing constraint; it is not an artefact of the model. It is also what makes the model
        //   valid at all, because the entity carries no member any convention could treat as a key.
        builder.HasKey(s => new { s.TabModuleId, s.SettingName }).HasName("PK_TabModuleSettings");

        // MIGRATION: no index is declared for this table, and the omission is deliberate rather than an
        //   oversight. A redundant non-clustered index stood over these same two columns, created
        //   alongside the table at 03.00.01:L739, and it was dropped at 03.00.03:L304 and never
        //   recreated. Declaring it now would assert an object the database does not have, and it
        //   would duplicate the clustered key above, which is precisely why the upgrade chain
        //   discarded it. Nothing here is made unique beyond that key either: the key is clustered
        //   over the very same two columns and already enforces uniqueness, so a second unique
        //   declaration would fabricate a constraint. The terminal set of keys, indexes and
        //   constraints on this table is the primary key above and the foreign key below, and nothing
        //   else.
        //
        // MIGRATION: the module-scoped settings table is IDENTICAL to this one in that respect, not
        //   contrasting. It too carries a real composite clustered primary key over its parent
        //   identifier and the setting name, and it too lost the same redundant index, at
        //   03.00.03:L312 one statement before this table lost its own. No distinction between the key
        //   situations of the two tables is drawn here or anywhere else in this folder.

        // MIGRATION: int NOT NULL [03.00.01:L723] - the leading column of the key above and the
        //   foreign key configured below. The store never supplies this column and the caller always
        //   does, and the sentinel Entity Framework Core compares a supplied value against is left at
        //   the CLR default of zero, which is correct here and must not be moved: dbo.TabModules
        //   numbers its rows IDENTITY (1, 1) [03.00.01:L21], so a placement identifier of zero exists
        //   in no installation and zero can never stand for a real parent. The module-scoped
        //   counterpart does move its sentinel, and the reason belongs to its parent rather than to
        //   either settings table - dbo.Modules numbers its rows from zero [01.00.00:L221], so zero is
        //   reachable there and a key value of zero has to remain distinguishable from an unsupplied
        //   one. The legacy absent-integer marker was -1, in
        //   Library/Components/Shared/Null.vb, and no marker of any kind is reintroduced on this
        //   column: whether the parent row exists is answered by the database, through the foreign key
        //   below.
        builder.Property(s => s.TabModuleId)
            .HasColumnName("TabModuleID")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(50) NOT NULL [03.00.01:L724] - the second column of the key above. The column
        // stores the name exactly as written and so preserves case, which makes two names differing
        // only in case two distinct rows. Whether a lookup folds case is a query-layer decision and is
        // deliberately not settled here. The width is declared so an over-long name is rejected at
        // this boundary instead of being truncated by the provider.
        builder.Property(s => s.SettingName)
            .HasColumnName("SettingName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: nvarchar(2000) NOT NULL [03.00.01:L725] - created at that width and never
        //   altered, so a setting carrying no value is stored as an empty string and never as a
        //   database null. That is the legacy contract preserved exactly rather than tidied: the
        //   absent-string marker in Library/Components/Shared/Null.vb is itself the empty string,
        //   which is why the column is non-nullable in the first place, and the reader loop at
        //   ModuleController.vb lines 1346-1350 put one in place of a null this column cannot hold.
        //   The property therefore stays non-nullable and this layer performs no marker translation
        //   in either direction. Relaxing it to admit a null as the safer-looking choice would change
        //   what round-trips. Both string columns here are Unicode, as every string column on this
        //   table is, so no non-Unicode declaration appears anywhere in this file; and the table
        //   carries no date-and-time column at all, so no temporal type appears either.
        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(2000)
            .IsRequired();

        // 03.00.09:L335-336 - ALTER TABLE TabModuleSettings WITH NOCHECK ADD CONSTRAINT
        // FK_{objectQualifier}TabModuleSettings_{objectQualifier}TabModules FOREIGN KEY
        // ([TabModuleID]) REFERENCES dbo.TabModules ([TabModuleID]) ON DELETE CASCADE NOT FOR
        // REPLICATION. That is the terminal form: the constraint was first added in exactly the same
        // shape at 03.00.01:L746-754, dropped at 03.00.09:L328-329, and re-added as above with only
        // its name qualified, so the empty object qualifier resolves it to
        // FK_TabModuleSettings_TabModules. WITH NOCHECK records that existing rows went unvalidated
        // when the constraint was created, and NOT FOR REPLICATION is a replication directive -
        // neither has a counterpart in the model, and both are left unrepresented across this folder,
        // as the sibling configurations also record.
        //
        // MIGRATION: the cascade is described as the database declares it rather than softened.
        //   Removing a placement already removes its settings, at the database and irrespective of
        //   this model, so mapping a lesser delete behaviour would make the model disagree with a
        //   constraint it cannot overrule. The key column is part of the primary key and NOT NULL, so
        //   the relationship is already required and nothing further is stated to make it so. The
        //   constraint name is pinned because convention would otherwise invent one built from the
        //   column name, which is not the name this database carries, and the relationship reuses the
        //   column mapped above so that no shadow property appears beside it. It is declared exactly
        //   once, here on the dependent end, and the principal end deliberately declares nothing for
        //   this edge: the assembly scan applies configurations in no guaranteed order, so an edge
        //   declared from both ends would take whichever delete behaviour ran last, with nothing to
        //   diagnose the ambiguity.
        builder.HasOne(s => s.TabModule)
            .WithMany(t => t.Settings)
            .HasForeignKey(s => s.TabModuleId)
            .HasConstraintName("FK_TabModuleSettings_TabModules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
