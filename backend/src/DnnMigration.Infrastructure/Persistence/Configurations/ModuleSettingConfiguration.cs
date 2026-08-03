using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="ModuleSetting"/> entity to the existing <c>dbo.ModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This is a genuine name-and-value store, which is what separates it from the portal settings the
/// legacy code only appeared to have. It exists in the schema, the upgrade chain rebuilds and then
/// re-keys it, and it carries a composite clustered primary key over the module identifier and the
/// setting name. The schema is externally owned: every name declared below describes a table that
/// already exists, and nothing in this assembly creates, alters or removes one.
/// </para>
/// <para>
/// <b>The primary key is a real database constraint, and the chain hides it twice over.</b> The
/// scripts are carriage-return delimited and they interpolate the object qualifier into constraint
/// names, so <c>PK_{objectQualifier}ModuleSettings</c> is split across a line break at
/// 02.00.01:L47-48 and a plain search for the resolved name finds nothing at all - which invites the
/// false conclusion that only a non-unique index stands over the two columns and that the key
/// declared here exists merely to satisfy the modelling requirement. Read with the templates
/// resolved, 02.00.01:L47-52 declares the key over <c>(ModuleID, SettingName)</c>, and the index is
/// the object the chain later REMOVED, at 03.00.03:L312. Only the cumulative terminal state of an
/// append-and-destroy chain means anything, which is why each citation below names the script that
/// last spoke on the point.
/// </para>
/// <para>
/// The value column is <c>nvarchar(2000)</c>, and 256 is the superseded baseline width.
/// 01.00.00:L353 created the column at 256, but 01.00.08:L6248-6286 destroys and rebuilds the whole
/// table through a temporary copy that widens the column to <c>nvarchar(2000) NOT NULL</c> at
/// 01.00.08:L6256, and no later script narrows it again: no statement anywhere in the chain alters a
/// column of this table. Binding 256 would reject values the legacy application accepts.
/// </para>
/// <para>
/// The class is internal, sealed and declares no constructor of its own, so the compiler supplies a
/// public parameterless one. The context discovers configurations by scanning this assembly for
/// exactly that shape, and a non-public constructor would leave this file inert with no diagnostic of
/// any kind. For this entity that failure is unrecoverable rather than merely quiet: it carries no
/// member any convention could treat as a key, so the model would not validate at all.
/// </para>
/// </remarks>
internal sealed class ModuleSettingConfiguration : IEntityTypeConfiguration<ModuleSetting>
{
    /// <summary>
    /// Applies the mapping for one stored setting of a module instance.
    /// </summary>
    /// <param name="builder">The builder for the module setting entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<ModuleSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy model had no type for these rows at all. They were reachable only as
        //   an untyped name-and-value bag returned by
        //   Library/Components/Modules/ModuleController.vb line 1237, one bag per module, filled by a
        //   hand-rolled loop over reader ordinals at lines 1251-1257 that substituted an empty string
        //   for a database null. The bag was declared on the abstract data surface as a reader at
        //   Library/Components/Providers/Data/DataProvider.vb line 142 and reached through six stored
        //   procedures whose names were built by concatenating a database owner and an object
        //   qualifier at
        //   Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb lines 734-750, a
        //   helper type that survives in the repository only as a compiled assembly. Writes went one
        //   name at a time at ModuleController.vb lines 1283, 1306 and 1318, each followed by a cache
        //   eviction. Nothing in that arrangement recorded what a name or a value was allowed to be,
        //   and a name stored empty read back identically to one that was never stored. The
        //   destination models the row as a row and lets the Entity Framework Core materialiser
        //   hydrate it, so no reflection-driven hydrator, no reader loop and no marker translation of
        //   any kind survives into this layer.
        //
        // Website/release.config:L354-355 registers the data provider with objectQualifier="" and
        // databaseOwner="dbo", so the table resolves to its unqualified name under the dbo schema and
        // every constraint name below resolves to its own bare form.
        builder.ToTable("ModuleSettings", "dbo");

        // MIGRATION: this composite key mirrors a constraint the database already carries. Resolved
        //   through the script templates, 02.00.01:L47-52 reads ALTER TABLE ModuleSettings ADD
        //   CONSTRAINT PK_ModuleSettings PRIMARY KEY CLUSTERED (ModuleID, SettingName) ON PRIMARY,
        //   and the single statement anywhere that removes that constraint lives in
        //   UnInstall.SqlDataProvider, which tears the entire installation down and is therefore
        //   silent on the terminal schema. The key is present in the schema this model binds to, the
        //   column order is the database's own rather than a choice made here, and the empty
        //   object qualifier resolves the constraint name to exactly PK_ModuleSettings. The name is
        //   pinned so the model carries the name the database has instead of one convention would
        //   invent. This declaration records an existing constraint; it is not an artefact of the
        //   model.
        builder.HasKey(s => new { s.ModuleId, s.SettingName }).HasName("PK_ModuleSettings");

        // MIGRATION: no index is declared for this table, and the omission is deliberate rather than
        //   an oversight. A redundant non-clustered index stood over these same two columns - created
        //   at 01.00.00:L571 and rebuilt at 01.00.08:L6270 once the table had been replaced - and it
        //   was dropped at 03.00.03:L312 and never recreated. The one later mention between those two
        //   events, at 02.00.00:L104, renames it to its own name and changes nothing. Declaring it
        //   now would assert an object the database does not have, and it would duplicate the
        //   clustered key above, which is precisely why the upgrade chain discarded it. The terminal
        //   set of keys, indexes and constraints on this table is the primary key above and the
        //   foreign key below, and nothing else.
        //
        // MIGRATION: the per-placement settings table is IDENTICAL to this one in that respect, not
        //   contrasting. It too carries a real composite clustered primary key over its parent
        //   identifier and the setting name, it too lost the same redundant index, and its value
        //   column is the same width. No distinction between the key situations of the two tables is
        //   drawn here or anywhere else in this folder.

        // MIGRATION: dbo.Modules.ModuleID is declared IDENTITY (0, 1) at 01.00.00:L221, so the first
        //   module of an installation is numbered zero and a zero in this column is a legitimate
        //   parent reference. It may never be read as absent, unset or defaulted, and no `== 0`,
        //   `<= 0` or `default(int)` heuristic may stand in for a presence check against it. The
        //   legacy model could not express that distinction at all: the absent-integer marker in
        //   Library/Components/Shared/Null.vb is -1, so a real identifier and a missing one were the
        //   same kind of value. Here the column is NOT NULL and the property is non-nullable, and
        //   whether the parent row exists is answered by the database through the foreign key below.
        //
        // The moved sentinel is what makes the paragraph above true in practice, and it is load
        // bearing rather than decorative. This property is part of the primary key AND part of a
        // foreign key, and for that combination the change tracker refuses to save a row whose value
        // it cannot distinguish from "not supplied": it compares the value against the property's
        // sentinel, which defaults to the CLR default of zero, and if they match while the principal
        // is not itself being tracked it reports the value as unknown. Every write path in this
        // solution assigns this scalar and leaves the navigation alone - Application services build
        // the row as `new ModuleSetting { ModuleId = ..., ... }` - so with the default sentinel a
        // module numbered zero could carry no settings at all. Moving the sentinel beyond the
        // reachable range of an IDENTITY (0, 1) column restores the schema's own meaning. This was
        // verified by execution against a database built from the terminal schema: with the sentinel
        // left at the CLR default the save fails outright, and with it moved the row inserts, reads
        // back and cascades. The value generation call records the other half of the same fact - the
        // store never supplies this column, the caller always does.
        //
        // This is the single respect in which this file and its per-placement counterpart differ, and
        // the difference belongs to their PARENTS rather than to these two tables, whose key
        // situations remain identical as recorded above. dbo.Modules seeds its identifier at zero
        // (01.00.00:L221), so zero is reachable here; dbo.TabModules seeds at one, so a placement
        // identifier of zero exists in no installation and no sentinel has to be moved there.
        builder.Property(s => s.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .HasSentinel(int.MinValue)
            .ValueGeneratedNever()
            .IsRequired();

        // 01.00.08:L6255 - SettingName nvarchar(50) NOT NULL, the second column of the key above. The
        // column stores the name exactly as written, so the case a caller supplied is what is read
        // back. Whether two names differing only in case are one row or two is NOT settled here and is
        // not settled by the schema either: no script in the chain assigns a collation to this column,
        // so comparison - and therefore key uniqueness - follows the database's own collation, which
        // on a default SQL Server installation is case-insensitive. Nothing in this model may assume
        // either answer: the application service reconciles submitted names case-insensitively, which
        // is the conservative reading and is correct under both collations.
        builder.Property(s => s.SettingName)
            .HasColumnName("SettingName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: the column is NOT NULL at its terminal width of 2000 - 01.00.08:L6256 - so a
        //   setting carrying no value is stored as an empty string and never as a database null. That
        //   is the legacy contract preserved exactly rather than tidied: the absent-string marker in
        //   Library/Components/Shared/Null.vb is the empty string, and the reader loop at
        //   ModuleController.vb lines 1253-1257 substituted one for a null the column cannot hold.
        //   The property therefore stays non-nullable and this layer performs no marker translation
        //   in either direction. Relaxing the property to accept a null to be safe, or binding the
        //   superseded baseline width of 256, would each change what round-trips.
        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(2000)
            .IsRequired();

        // 01.00.08:L6277-6285 - ALTER TABLE dbo.ModuleSettings WITH NOCHECK ADD CONSTRAINT
        // FK_ModuleSettings_Modules FOREIGN KEY (ModuleID) REFERENCES dbo.Modules (ModuleID) ON
        // DELETE CASCADE NOT FOR REPLICATION. That is the terminal form: the constraint was first
        // added at 01.00.00:L790-796, dropped at 01.00.08:L6248-6249 so the table could be rebuilt
        // around a widened value column, and re-added in the shape above; the only later mention is a
        // rename to its own name at 02.00.00:L145. WITH NOCHECK records that existing rows went
        // unvalidated when the constraint was created, and NOT FOR REPLICATION is a replication
        // directive - neither has a counterpart in the model, and both are left unrepresented across
        // this folder, as the sibling configurations also record.
        //
        // MIGRATION: the cascade is described as the database declares it rather than softened.
        //   Removing a module already removes its settings, at the database and irrespective of this
        //   model, so mapping a lesser delete behaviour would make the model disagree with a
        //   constraint it cannot overrule. The key column is part of the primary key and NOT NULL, so
        //   the relationship is required and needs no further statement to make it so. The constraint
        //   name is pinned because convention would otherwise invent one built from the column name,
        //   which is not the name this database carries, and the relationship reuses the column
        //   mapped above so that no shadow property appears. It is declared exactly once, here on the
        //   dependent end, and the principal deliberately leaves the inverse end undeclared: the
        //   assembly scan applies configurations in no guaranteed order, so a relationship declared
        //   from both ends would take whichever delete behaviour ran last, with nothing to diagnose
        //   the ambiguity.
        builder.HasOne(s => s.Module)
            .WithMany(m => m.Settings)
            .HasForeignKey(s => s.ModuleId)
            .HasConstraintName("FK_ModuleSettings_Modules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
