namespace DnnMigration.Domain.Entities;

// MIGRATION: ==================================================================================
// SOURCE
//   The legacy model had no type for this row at all. Library/Components/Modules/ModuleInfo.vb
//   (936 lines) declares 58 properties and not one of them is a setting. A module's settings were
//   reachable only as an untyped bag: Library/Components/Modules/ModuleController.vb line 1237,
//   "Public Function GetModuleSettings(ByVal ModuleId As Integer) As Hashtable", written back one
//   name at a time by UpdateModuleSetting (line 1283), DeleteModuleSetting (line 1306) and
//   DeleteModuleSettings (line 1318), re-exposed to pages by
//   Library/Components/Portal/PortalSettings.vb line 1000, and declared on the abstract data
//   surface as a reader at Library/Components/Providers/Data/DataProvider.vb line 142.
//
//   A Hashtable keyed by object and holding object is not a contract. Every read needed a cast, an
//   absent name and a name stored with an empty value were indistinguishable once read through
//   Library/Components/Shared/Null.vb (whose NullString sentinel is the empty string rather than
//   null), and nothing recorded what a name or a value was allowed to be. Rule T8 replaces the
//   workaround with the primitive, so the row is modelled as a row: one instance per stored
//   setting, three columns, and nothing else.
//
// TERMINAL COLUMN SET - the three scalars declared here, and no others
//   Derived by replaying every CREATE, ALTER and DROP TABLE naming this table across the upgrade
//   scripts in Website/Providers/DataProviders/SqlDataProvider, matched case-insensitively and in
//   all four naming forms the chain uses - bare, dbo.-qualified, [dbo].[...]-bracketed and
//   {databaseOwner}{objectQualifier}-templated - because a single-form, case-sensitive search
//   reports objects of this chain as absent when they are present:
//     01.00.00:350-354   CREATE TABLE [dbo].[ModuleSettings] - ModuleID int NOT NULL,
//                        SettingName nvarchar(50) NOT NULL, SettingValue nvarchar(256) NOT NULL.
//                        No primary key is declared here, only the non-unique index at line 571.
//     01.00.00:790-796   ADD CONSTRAINT FK_ModuleSettings_Modules FOREIGN KEY (ModuleID)
//                        REFERENCES [dbo].[Modules] (ModuleID) ON DELETE CASCADE
//                        NOT FOR REPLICATION.
//     01.00.08:6248-6286 THE TABLE IS DESTROYED AND REBUILT. The foreign key is dropped, a
//                        dbo.Tmp_ModuleSettings is created with SettingValue widened to
//                        nvarchar(2000) NOT NULL (line 6256), the rows are copied under TABLOCKX,
//                        DROP TABLE dbo.ModuleSettings executes, sp_rename renames the temporary
//                        table over it, the index is recreated over (ModuleID, SettingName), and
//                        the foreign key is re-added with the same cascade and replication
//                        clauses.
//     02.00.00:36,104,145 sp_rename applies the object qualifier to the table, its index and its
//                        foreign key. This installation configures objectQualifier="" and
//                        databaseOwner="dbo" (Website/release.config lines 351-355), so the names
//                        are unchanged in practice.
//     02.00.01:47-52     ADD CONSTRAINT PK_{objectQualifier}ModuleSettings PRIMARY KEY CLUSTERED
//                        (ModuleID, SettingName). This, and nothing earlier, is where the key
//                        becomes a key.
//     03.00.03:312       DROP INDEX on IX_{objectQualifier}ModuleSettings, made redundant by that
//                        clustered key.
//   Nothing afterwards touches the table: no ALTER COLUMN in the chain names it, and no later
//   script narrows the value column. The terminal shape is therefore
//   (ModuleID int NOT NULL, SettingName nvarchar(50) NOT NULL, SettingValue nvarchar(2000)
//   NOT NULL), keyed (ModuleID, SettingName).
//
// THE VALUE COLUMN IS nvarchar(2000), AND 256 IS THE SUPERSEDED BASELINE WIDTH
//   This is the one fact about this table that a baseline-only reading gets wrong, and it is
//   stated separately because the mistake is silent: a mapping or a validator capped at 256
//   refuses values that the legacy application stored and still stores, which Rule T5 forbids.
//   Three independent authorities agree on 2000. The rebuilt column declares
//   SettingValue nvarchar(2000) NOT NULL at 01.00.08:6256. The terminal writers declare
//   @SettingValue nvarchar(2000) - UpdateModuleSetting at 01.00.08:6295 and, re-created in
//   templated form, AddModuleSetting and UpdateModuleSetting at 02.00.00:4147 and 02.00.00:4171.
//   And the terminal reader, GetModuleSettings at 04.04.00:718, projects the column without
//   narrowing it. The 256-wide setting procedures that do survive the chain - AddHostSetting and
//   UpdateHostSetting at 03.00.12:404 and 03.00.12:424 - belong to dbo.HostSettings, a different
//   table with a different key, and are not evidence about this one.
//
// OBLIGATIONS ON THE INFRASTRUCTURE MAPPING (this layer declares no mapping of its own)
//   - Declare the composite primary key (ModuleID, SettingName) explicitly. The schema holds no
//     surrogate identity and this type does not emulate one, so there is nothing for convention to
//     discover: without an explicit key declaration the entity has no key at all.
//   - Map ModuleId with the column name ModuleID. The property is spelled idiomatically and the
//     column is not, so convention would look for a column named ModuleId and fail.
//   - Bind SettingValue at 2000, not at 256, for the reason set out above.
//   - Preserve the existing foreign key from ModuleID to Modules.ModuleID with
//     ON DELETE CASCADE NOT FOR REPLICATION. Removing a module removes its settings at the
//     database level, and the replication clause is part of the constraint as the schema declares
//     it. The schema is immutable for this migration (Rule T4), so this file describes a table
//     that already exists rather than defining one.
//   - Modules.ModuleID is IDENTITY(0, 1), so zero identifies the first module of an installation
//     rather than meaning "not written yet". A mapping that read the CLR default of ModuleId as
//     "the principal row has still to be inserted" would refuse every setting belonging to that
//     module, which is why the mapping declares an out-of-range sentinel instead of relying on the
//     default.
//
// NO DICTIONARY BEHAVIOUR, AND NO SURROGATE KEY
//   Turning these rows back into a lookup is projection, and projection belongs to a repository or
//   an application service - not here. Nothing in this type parses, converts, defaults, caches or
//   validates a value, and nothing indexes one setting by another's name: a setting is text in the
//   database and it is text here, and the meaning of a particular name belongs to whichever
//   service reads it. Equally, no Identity, Id or ModuleSettingId member exists. Inventing one
//   would assert a column the table does not have, and it would compete with the explicit key
//   declaration required above.
// =============================================================================================

/// <summary>
/// One name and value pair stored against a module instance: the persistence shape of a single row
/// of the legacy <c>dbo.ModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This is a genuine key and value store, which is what distinguishes it from the portal settings
/// the legacy code only appeared to have. It exists in the schema, the upgrade chain rebuilds and
/// then re-keys it, and it carries a composite clustered primary key over <see cref="ModuleId"/>
/// and <see cref="SettingName"/>. Settings that vary with where a module is placed are a separate
/// store and hang off <see cref="TabModuleSetting"/> instead.
/// </para>
/// <para>
/// The type deliberately has no base class and no identity member. Every other entity in this
/// model has one identity column and derives from the identity-bearing base type; this table has
/// none, so declaring an identity here would assert a column that does not exist. Identity for
/// these rows is the composite key itself, and keys are declared by the persistence layer.
/// </para>
/// <para>
/// All three columns are <c>NOT NULL</c>, so all three properties are non-nullable, and the
/// navigation is non-nullable because the column that carries it is. Nothing is optional and no
/// value stands in for absence: a setting that is not stored has no row. That is precisely the
/// gain over the legacy bag of names, in which a missing name and a name holding an empty string
/// read back identically.
/// </para>
/// </remarks>
public sealed class ModuleSetting
{
    /// <summary>
    /// Gets or sets the <c>ModuleID</c> column: <c>int NOT NULL</c>, the first column of the
    /// composite primary key and the foreign key to <c>dbo.Modules</c>.
    /// </summary>
    /// <remarks>
    /// MIGRATION: zero is a real module. <c>dbo.Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c>, so
    /// the first module of an installation is numbered zero and this value may never be read as
    /// "no module". The constraint that carries it, <c>FK_ModuleSettings_Modules</c>, is declared
    /// <c>ON DELETE CASCADE NOT FOR REPLICATION</c>, so removing a module removes its settings
    /// without this layer doing anything - and the Infrastructure mapping must preserve that
    /// constraint, along with the legacy <c>ModuleID</c> spelling of this idiomatically named
    /// property.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// Gets or sets the <c>SettingName</c> column: <c>nvarchar(50) NOT NULL</c>, the second column
    /// of the composite primary key.
    /// </summary>
    /// <remarks>
    /// The column stores the name exactly as written, so it preserves case, and two names
    /// differing only in case are two distinct rows. Whether a lookup folds case is a query-layer
    /// decision and is deliberately not settled here.
    /// </remarks>
    public string SettingName { get; set; }

    /// <summary>
    /// Gets or sets the <c>SettingValue</c> column: <c>nvarchar(2000) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <b>the width is the terminal 2000, and 256 is the superseded baseline.</b> The
    /// column was created <c>nvarchar(256) NOT NULL</c> by
    /// <c>01.00.00.SqlDataProvider</c> line 353, but <c>01.00.08.SqlDataProvider</c> lines
    /// 6248-6286 rebuild the whole table - dropping the foreign key, creating
    /// <c>dbo.Tmp_ModuleSettings</c> with <c>SettingValue nvarchar(2000) NOT NULL</c> at line
    /// 6256, copying the rows, dropping the original and renaming the replacement over it - and no
    /// later script narrows it again. The terminal <c>UpdateModuleSetting</c> and
    /// <c>AddModuleSetting</c> procedures declare <c>@SettingValue nvarchar(2000)</c>
    /// (<c>01.00.08</c> line 6295, <c>02.00.00</c> lines 4147 and 4171), which corroborates it. A
    /// mapping or validator that caps this value at 256 rejects data the legacy application
    /// accepts, so only the terminal width is authoritative.
    /// </remarks>
    public string SettingValue { get; set; }

    /// <summary>
    /// Gets or sets the module this setting belongs to.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>ModuleID</c> is <c>NOT NULL</c> and its
    /// constraint cascades: a setting cannot outlive its module, so it cannot exist without one.
    /// It is nonetheless left <see langword="null"/> by any query that does not load it, which is
    /// why reading it defensively is still correct. The inverse end is
    /// <see cref="Module.Settings"/>.
    /// </remarks>
    public Module Module { get; set; }
}
