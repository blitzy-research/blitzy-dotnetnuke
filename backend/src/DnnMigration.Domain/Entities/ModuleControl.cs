using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: legacy type DotNetNuke.Entities.Modules.ModuleControlInfo
//   (Library/Components/Modules/ModuleControlInfo.vb, 133 lines) becomes this entity. The
//   ModuleControls slice of the flattened Library/Components/Modules/ModuleInfo.vb is answered here
//   rather than there: that class carried ControlSrc, ControlType, ControlTitle, IconFile and
//   SupportsPartialRendering (fields at lines 87-91, properties at lines 239 and 500-541) beside
//   module and placement state, because it modelled a four-table join rather than a table. The
//   "...Info" suffix existed only to separate a data carrier from its static companion, a
//   distinction the target draws with the layer boundary, so the suffix carries no information and
//   is dropped.
//
// MIGRATION: constructs deleted rather than translated, each measured redundant:
//   - the six Imports at lines 21-26 (System, System.Configuration, System.Data,
//     System.Globalization, System.IO and System.Xml). Not one is referenced by the class body, and
//     the three that would matter - System.Data, System.Xml and System.Configuration - are exactly
//     the couplings this layer forbids. Implicit usings cover what remains.
//   - the ten private backing fields at lines 32-41 together with the ten Get/Set property blocks
//     at lines 46-127 that wrapped them. Auto-properties express the identical contract.
//   - the parameterless constructor at lines 43-44. Its body is empty, so it established nothing.
//   - the initialiser on the field at line 41, which seeded SupportsPartialRendering from the legacy
//     Boolean sentinel. That sentinel is literally False (Library/Components/Shared/Null.vb), which
//     the runtime already guarantees for a Boolean field, and False is also the column's schema
//     default - so the initialiser restated the default instead of establishing it, and nothing here
//     needs to assert it.
//   - the routine that hydrated this type from a forward-only data reader (FillModuleControlInfo in
//     Library/Components/Modules/, lines 75-104, whose ten sentinel-translating assignments run from
//     line 89 to line 98) and the ArrayList-returning collection filler above it. The
//     object-relational mapper's materialiser replaces both, so this file carries no
//     hydration member, no collection wrapper and no sentinel translation. One quirk disappears with
//     the mechanism rather than being ported or repaired: line 94 read IconFile while passing
//     ControlKey as the value whose type selects the substituted sentinel. Both are strings, so the
//     sentinel chosen was the same one and the result was correct by accident.
//
// MIGRATION: ControlType stays a raw int, and this file neither declares nor references an
//   enumeration for it. The legacy backing field is already an Integer
//   (Library/Components/Modules/ModuleControlInfo.vb line 38); only the public property cast it
//   (lines 94-101), to the access-level enumeration declared at
//   Library/Components/Security/PortalSecurity.vb line 45 - a type this migration excludes. Four
//   measurements say the persisted ordinal must travel untranslated:
//     1. The data boundary already treats it as an integer. The abstract provider surface
//        Library/Components/Providers/Data/DataProvider.vb declares "ControlType As Integer" on both
//        AddModuleControl (line 181) and UpdateModuleControl (line 182), and the terminal column is
//        int NOT NULL.
//     2. Persisted ordinals are not a zero-based range. 04.06.00.SqlDataProvider inserts
//        ControlType = -1 at lines 24-26, 683-685 and 691-693, and ControlType = 3 at line 1054, so
//        a negative ordinal is ordinary production data.
//     3. The terminal SQL itself filters on a negative ordinal: 02.02.00.SqlDataProvider line 545
//        excludes rows with "ControlType <> -2". A CLR type that could not hold -2 would silently
//        change which rows that predicate describes.
//     4. The legacy read path already round-tripped the number through a string - Enum.Parse over
//        Convert.ToString of the column, at line 95 of the routine named above. Enum.Parse accepts a
//        numeric string and returns that number whether or not a member carries it, so the raw
//        ordinal survived the cast unchanged. A plain int reproduces that outcome exactly and removes
//        a string conversion that could only lose.
//   This note is annotation, not licence. The ordinals are neither renamed, renumbered nor
//   reinterpreted here, and that excluded enumeration must not be introduced into this layer to
//   "tidy" the property: doing so would both re-couple the domain to an out-of-scope tree and put a
//   named-member contract in front of values the database is free to hold.
//
// MIGRATION: absence is expressed with nullable CLR types, never with the legacy sentinels held in
//   Library/Components/Shared/Null.vb (-1 for an integer, the empty string for text, False for a
//   Boolean). The legacy in-memory model could not tell absence from data, because every column was
//   read through Null.SetNull: a null ModuleDefID reached callers as -1 and a null ControlKey as "".
//   The database never lost that distinction and neither does this entity - ModuleDefinitionId and
//   ViewOrder are int? and the four text columns are string?, exactly as the terminal schema declares
//   them. The distinction is load-bearing rather than cosmetic: 02.02.00.SqlDataProvider lines
//   543-544 compare ControlKey and ModuleDefId with explicit "is null" arms, and the terminal read at
//   04.05.00.SqlDataProvider line 1491 identifies the default control of a definition by
//   "MC.ControlKey IS NULL", which an empty string would not satisfy. Where a legacy sentinel remains
//   observable in a published contract it is restated at the DTO boundary; it is never reintroduced
//   here. SupportsPartialRendering is the member that looks like a sentinel and is not - its column
//   is NOT NULL with a default of 0, so false is an ordinary Boolean default.
//
// MIGRATION: HelpUrl is spelled with the idiomatic casing, and the legacy HelpURL spelling is not
//   preserved. This costs nothing at the boundary, because the SQL identifier is itself spelled
//   HelpUrl - 02.02.00.SqlDataProvider line 455 adds "HelpUrl [nvarchar] (200) NULL" - so the
//   ModuleControlConfiguration column binding maps one spelling onto the same spelling. Only the
//   legacy VB property disagreed, and its reads were case-insensitive anyway.
//
// MIGRATION: this entity carries no attribute of any kind and takes no dependency, so nothing here
//   states how it is stored: the table name, every column name, the key, the unique index, the
//   cascading foreign key and the Boolean default all belong to ModuleControlConfiguration in the
//   Infrastructure layer, which the terminal contract below exists to brief. The divergences above
//   are to be recorded in MIGRATION_NOTES.md as an appended entry under the module domain, worded as:
//   "ModuleControlInfo becomes the ModuleControl entity over dbo.ModuleControls. Three deliberate
//   differences. Its nullable columns - ModuleDefID, ControlKey, ControlTitle, ControlSrc, IconFile,
//   ViewOrder and HelpUrl - are modelled as nullable CLR types rather than as the legacy sentinels
//   (-1 and the empty string) that Null.SetNull produced on every read, which restores a distinction
//   the terminal SQL relies on. ControlType keeps the raw persisted integer ordinal instead of the
//   access-level enumeration the legacy property cast to, because that enumeration lives in an
//   excluded tree, its ordinals include negative values, and the terminal SQL filters on one of them.
//   HelpURL is spelled HelpUrl, matching the SQL identifier the 02.02.00 script added." Append to
//   that document only, and never edit mkdocs.yml: its nav entries resolve relative to docs/ and
//   cannot address a file that sits at the root of the checkout, so adding one there would break the
//   documentation build.

/// <summary>
/// One user-interface entry point that a module definition publishes - the row an administrator
/// picks when choosing which view, edit or settings surface of a module to reach.
/// </summary>
/// <remarks>
/// <para>
/// The table exists because of the 02.00.00 upgrade, whose own comment above the statement reads
/// "split module definitions": the fixed <c>DesktopSrc</c>, <c>MobileSrc</c> and <c>EditSrc</c> trio
/// that <c>dbo.ModuleDefinitions</c> used to carry was replaced by an open-ended set of control rows,
/// so a definition may publish as many entry points as it needs. <c>ControlSrc</c> holds a Web Forms
/// control path in the legacy data and is carried through untouched; nothing in the target loads it,
/// because the presentation layer is Angular, and the rows are mapped because the module
/// administration screens list the available control keys.
/// </para>
/// <para>
/// A row need not belong to a definition. <see cref="ModuleDefinitionId"/> is nullable, and the
/// host-level controls that <c>04.06.00.SqlDataProvider</c> inserts (lines 24-26, 683-685 and
/// 691-693) leave it unset, which is why the navigation below is nullable too.
/// </para>
/// <para>
/// <b>Terminal mapping contract for <c>ModuleControlConfiguration</c>.</b> The legacy schema is
/// immutable for this migration, so the table below is the measured terminal state of
/// <c>dbo.ModuleControls</c> after all eighty-eight upgrade scripts in
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> have been replayed in order - not the
/// table as first created, which is missing two columns and gets a third one's width wrong. The table
/// does not exist in the <c>01.00.00</c> baseline at all: exactly three scripts touch it, and nothing
/// after <c>04.05.00</c> alters it. Every column is bound explicitly with
/// <c>HasColumnName</c> and the table with <c>ToTable("ModuleControls", "dbo")</c>, because the
/// legacy installation runs with an empty object qualifier and <c>dbo</c> as its database owner.
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Column</term>
///     <description>Terminal declaration, and the script that produced it</description>
///   </listheader>
///   <item>
///     <term>ModuleControlID</term>
///     <description>
///     <c>int IDENTITY(1, 1) NOT NULL</c>, created by <c>02.00.00</c> line 4997; the
///     <c>PK_ModuleControls</c> primary key follows, clustered, at lines 5009-5013. Mapped from
///     <see cref="ModuleControlId"/> and generated on add.
///     </description>
///   </item>
///   <item>
///     <term>ModuleDefID</term>
///     <description>
///     <c>int NULL</c> (line 4998), constrained by <c>FK_ModuleControls_ModuleDefinitions</c> at
///     lines 5025-5031, declared <c>ON DELETE CASCADE NOT FOR REPLICATION</c>. Optional and
///     cascading at once, so the configuration must model the relationship as optional without
///     softening the delete rule. Mapped from <see cref="ModuleDefinitionId"/>.
///     </description>
///   </item>
///   <item>
///     <term>ControlKey</term>
///     <description>
///     <c>nvarchar(50) NULL</c>. Created as <c>nvarchar(20)</c> by <c>02.00.00</c> line 4999 and
///     widened by <c>02.02.00</c> line 460, which the same script's stored procedure then declares as
///     its parameter width at line 467. Fifty is therefore the width to map; twenty is the as-created
///     width and would truncate a legal key. Mapped from <see cref="ControlKey"/>.
///     </description>
///   </item>
///   <item>
///     <term>ControlTitle</term>
///     <description><c>nvarchar(50) NULL</c> (line 5000). Mapped from <see cref="ControlTitle"/>.</description>
///   </item>
///   <item>
///     <term>ControlSrc</term>
///     <description>
///     <c>nvarchar(256) NULL</c> (line 5001). Mapped from <see cref="ControlSrc"/>.
///     </description>
///   </item>
///   <item>
///     <term>IconFile</term>
///     <description><c>nvarchar(100) NULL</c> (line 5002). Mapped from <see cref="IconFile"/>.</description>
///   </item>
///   <item>
///     <term>ControlType</term>
///     <description>
///     <c>int NOT NULL</c> (line 5003), with no default. Required, and mapped from
///     <see cref="ControlType"/> as a plain integer for the reasons annotated above.
///     </description>
///   </item>
///   <item>
///     <term>ViewOrder</term>
///     <description>
///     <c>int NULL</c> (line 5004). Mapped from <see cref="ViewOrder"/>.
///     </description>
///   </item>
///   <item>
///     <term>HelpUrl</term>
///     <description>
///     <c>nvarchar(200) NULL</c>, added by <c>02.02.00</c> line 455 - absent when the table was
///     created. Mapped from <see cref="HelpUrl"/>.
///     </description>
///   </item>
///   <item>
///     <term>SupportsPartialRendering</term>
///     <description>
///     <c>bit NOT NULL</c> with a default of <c>0</c> carried by the named constraint
///     <c>DF_ModuleControls_SupportsPartialRendering</c>, added under an existence guard by
///     <c>04.05.00</c> lines 1258-1260 and likewise absent at creation. The configuration must
///     reproduce the default so an insert that omits the column still lands on false rather than on a
///     database error. Mapped from <see cref="SupportsPartialRendering"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// Ten columns, and ten persisted properties below - no more. The count is corroborated independently
/// of the schema by the terminal stored procedures and the provider surface that calls them:
/// <c>AddModuleControl</c> (<c>04.05.00</c> line 1270) takes these columns less the generated
/// identity, and <c>UpdateModuleControl</c> (<c>04.05.00</c> line 1391) takes all ten, matching
/// <c>DataProvider.vb</c> lines 181-182 argument for argument. Anything absent from the table above
/// is absent from this entity: no audit columns, since the table declares none, which is also why
/// this type derives from <see cref="Entity{TId}"/> rather than from the auditable base.
/// </para>
/// <para>
/// The type is a persistence-shaped record of state and carries no behaviour, no validation and no
/// formatting. Reads, writes, cache invalidation and the manifest parsing that used to create these
/// rows all belong to other layers in the target, and the unique index over
/// <c>(ModuleDefID, ControlKey, ControlSrc)</c> at lines 5016-5022 is enforced by the database and
/// declared by the configuration - never re-implemented here as a guard clause.
/// </para>
/// </remarks>
public sealed class ModuleControl : Entity<int>
{
    /// <summary>
    /// Gets or sets the installation-wide identity of this control row.
    /// </summary>
    /// <value>
    /// The value of the <c>ModuleControlID</c> column: a database-generated identity seeded at 1.
    /// </value>
    /// <remarks>
    /// The property keeps the column's meaning while spelling the suffix as <c>Id</c>, which is the
    /// casing the rest of the target uses; the column name itself is unchanged, and
    /// <c>ModuleControlConfiguration</c> binds this property to <c>ModuleControlID</c> explicitly and
    /// names it in its key declaration. Because the column is <c>IDENTITY(1, 1)</c> the configuration
    /// marks it generated on add, so a value assigned before insertion is not honoured. The seed of 1
    /// also means no identity of this aggregate can collide with the legacy -1 marker for an absent
    /// integer, unlike four other in-scope tables whose identities seed at -1 or 0.
    /// </remarks>
    public int ModuleControlId { get; set; }

    /// <summary>
    /// Gets the value that identifies this entity for the purposes of equality.
    /// </summary>
    /// <value>The value of <see cref="ModuleControlId"/>.</value>
    /// <remarks>
    /// Required by <see cref="Entity{TId}"/> and used for equality alone. It is get-only, carries no
    /// attribute and is mapped to nothing: it is computed from <see cref="ModuleControlId"/> and has
    /// no backing field, so the model builder does not discover it, and
    /// <c>ModuleControlConfiguration</c> names <see cref="ModuleControlId"/> in its key declaration
    /// instead.
    /// </remarks>
    public override int Identity => ModuleControlId;

    /// <summary>
    /// Gets or sets the identity of the module definition that publishes this control, or
    /// <see langword="null"/> when the control belongs to no definition.
    /// </summary>
    /// <value>
    /// The value of the nullable <c>ModuleDefID</c> column: the <c>ModuleDefID</c> of the owning
    /// <see cref="Entities.ModuleDefinition"/>, or <see langword="null"/> for a host-level control.
    /// </value>
    /// <remarks>
    /// <para>
    /// The foreign key introduced by the 02.00.00 split, and the property that makes this entity the
    /// dependent half of the definition-to-control relationship. Nullability here is real rather than
    /// defensive: the three controls inserted by <c>04.06.00.SqlDataProvider</c> at lines 24-26,
    /// 683-685 and 691-693 name no definition, and <c>02.02.00.SqlDataProvider</c> line 544 compares
    /// the column with an explicit <c>is null</c> arm precisely because the database can hold no
    /// value there.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy property was a non-nullable Integer, so a database <c>NULL</c> reached
    /// callers as -1. Here absence is <see langword="null"/> and -1 is never written in its place.
    /// </para>
    /// </remarks>
    public int? ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the key that selects this control among the ones its definition publishes, such
    /// as <c>Edit</c> or <c>Settings</c>.
    /// </summary>
    /// <value>
    /// The value of the <c>ControlKey</c> column. Optional, at most 50 characters in the terminal
    /// schema.
    /// </value>
    /// <remarks>
    /// <para>
    /// One of the three columns in the unique index, so a definition may publish the same key from a
    /// different source, and the same source under a different key, but not the same pair twice.
    /// </para>
    /// <para>
    /// MIGRATION: an absent key is <see langword="null"/>, not the empty string that the legacy
    /// sentinel produced, because the schema attaches meaning to <c>NULL</c> specifically - the
    /// terminal read at <c>04.05.00.SqlDataProvider</c> line 1491 selects a definition's default
    /// control with <c>WHERE (MC.ControlKey IS NULL)</c>, and <c>02.02.00.SqlDataProvider</c> line
    /// 543 pairs an <c>is null</c> test with the equality test rather than relying on one or the
    /// other. An empty string would satisfy neither.
    /// </para>
    /// </remarks>
    public string? ControlKey { get; set; }

    /// <summary>
    /// Gets or sets the title presented for this control, such as <c>Account Logout</c>.
    /// </summary>
    /// <value>
    /// The value of the <c>ControlTitle</c> column. Optional, at most 50 characters.
    /// </value>
    /// <remarks>
    /// MIGRATION: absent is <see langword="null"/> rather than the empty string the legacy sentinel
    /// produced. The wording itself is data and is passed through untouched - the localisation
    /// mechanism the legacy screens used is not ported, so nothing here rewrites, trims or
    /// defaults it.
    /// </remarks>
    public string? ControlTitle { get; set; }

    /// <summary>
    /// Gets or sets the source path recorded for this control, such as
    /// <c>Admin/Authentication/Logoff.ascx</c>.
    /// </summary>
    /// <value>
    /// The value of the <c>ControlSrc</c> column. Optional, at most 256 characters.
    /// </value>
    /// <remarks>
    /// MIGRATION: a legacy Web Forms control path, stored and returned verbatim. It is data to this
    /// migration and nothing more: the target never resolves, loads or validates it, because the
    /// presentation layer is Angular and the Web Forms surface is out of scope. It is nonetheless one
    /// of the three columns in the unique index, so it is preserved exactly as stored rather than
    /// normalised - rewriting a path would change which rows collide.
    /// </remarks>
    public string? ControlSrc { get; set; }

    /// <summary>
    /// Gets or sets the path of the icon shown for this control.
    /// </summary>
    /// <value>
    /// The value of the <c>IconFile</c> column. Optional, at most 100 characters.
    /// </value>
    /// <remarks>
    /// MIGRATION: absent is <see langword="null"/> rather than the empty string the legacy sentinel
    /// produced, and the value is passed through unresolved - turning a stored relative path into a
    /// reachable asset is a presentation concern.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets the persisted access-level ordinal that says how privileged a caller must be to
    /// reach this control.
    /// </summary>
    /// <value>
    /// The value of the <c>ControlType</c> column: a required integer, stored and returned exactly as
    /// the database holds it.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: deliberately a plain <see cref="int"/> rather than an enumeration. The legacy
    /// backing field was already an Integer and only the public property cast it, to a type in an
    /// excluded tree; the data provider surface passes the value as an Integer in both directions;
    /// production data holds negative ordinals as well as non-negative ones; and the terminal SQL
    /// filters on one of the negative values directly. Every citation is in the annotation block at
    /// the head of this file.
    /// </para>
    /// <para>
    /// Consequently no value here is reserved, checked or interpreted, and none may be. Code that
    /// needs to act on the ordinal belongs in the layer that owns authorisation, which can map it
    /// deliberately and in one place; putting a named-member contract in front of it here would
    /// misrepresent a column the database is free to fill with any integer.
    /// </para>
    /// </remarks>
    public int ControlType { get; set; }

    /// <summary>
    /// Gets or sets the ordinal that sequences the controls of one definition, or
    /// <see langword="null"/> when no order was recorded.
    /// </summary>
    /// <value>
    /// The value of the nullable <c>ViewOrder</c> column.
    /// </value>
    /// <remarks>
    /// MIGRATION: the legacy property was a non-nullable Integer, so an unrecorded order reached
    /// callers as -1 and sorted ahead of every recorded one. Here it is <see langword="null"/>, which
    /// is what the column holds and what the terminal <c>order by ViewOrder</c> at
    /// <c>02.02.00.SqlDataProvider</c> line 546 actually sorts on; the sort belongs to the query, not
    /// to this entity.
    /// </remarks>
    public int? ViewOrder { get; set; }

    /// <summary>
    /// Gets or sets the address of the help document offered beside this control.
    /// </summary>
    /// <value>
    /// The value of the <c>HelpUrl</c> column. Optional, at most 200 characters.
    /// </value>
    /// <remarks>
    /// MIGRATION: named with the idiomatic casing, which is also the casing of the SQL identifier
    /// added by <c>02.02.00.SqlDataProvider</c> line 455; the legacy VB property was the only place
    /// that spelled the suffix in capitals, and the column binding in
    /// <c>ModuleControlConfiguration</c> is an identity mapping either way. Absent is
    /// <see langword="null"/>, and the value is stored as text without being parsed or validated as a
    /// URL - the legacy data was never constrained to be one.
    /// </remarks>
    public string? HelpUrl { get; set; }

    /// <summary>
    /// Gets or sets whether the legacy control declared support for partial page rendering.
    /// </summary>
    /// <value>
    /// The value of the <c>SupportsPartialRendering</c> column: required, defaulting to
    /// <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// MIGRATION: <see langword="false"/> here is an ordinary Boolean default and not a domain
    /// sentinel. The column is <c>NOT NULL</c> with a default of <c>0</c>, and true is genuine data -
    /// <c>04.05.00.SqlDataProvider</c> line 1832 sets it for one shipped control immediately after
    /// adding the column. The flag describes the legacy Web Forms behaviour of the row and is
    /// preserved for fidelity; the target reads and writes it without acting on it, because partial
    /// rendering has no counterpart in an Angular client.
    /// </remarks>
    public bool SupportsPartialRendering { get; set; }

    /// <summary>
    /// Gets or sets the module definition that publishes this control.
    /// </summary>
    /// <value>
    /// The owning <see cref="Entities.ModuleDefinition"/>, or <see langword="null"/> when
    /// <see cref="ModuleDefinitionId"/> is <see langword="null"/> or the relationship has not been
    /// loaded.
    /// </value>
    /// <remarks>
    /// The inverse of <see cref="Entities.ModuleDefinition.ModuleControls"/>, and the reason nothing
    /// in the target needs the legacy parse-time correlation number that used to stand in for an
    /// unsaved definition's key: the relationship is an object reference. It is nullable because the
    /// foreign-key column is, so a null here is ambiguous between a host-level control and a
    /// relationship that was simply not included in the query - only <see cref="ModuleDefinitionId"/>
    /// distinguishes the two, and code that must know should read it rather than test this member.
    /// </remarks>
    public ModuleDefinition? ModuleDefinition { get; set; }
}
