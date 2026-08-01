using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: legacy type DotNetNuke.Entities.Modules.Definitions.ModuleDefinitionInfo
//   (Library/Components/Modules/ModuleDefinitionInfo.vb, 85 lines) becomes this entity. The legacy
//   "...Info" suffix existed only to separate a data-carrying class from its static
//   "...Controller" companion - here Library/Components/Modules/ModuleDefinitionController.vb - and
//   the target draws that distinction with the layer boundary instead, so the suffix carries no
//   information and is dropped. The legacy namespace segment "Definitions" is dropped with it: it
//   held exactly two types, and a namespace that partitions two classes of one aggregate adds
//   nesting without adding meaning.
//
// MIGRATION: constructs deleted rather than translated, each measured unused or redundant:
//   - the six Imports at lines 21-26 (System, System.Configuration, System.Data,
//     System.Globalization, System.IO and System.Xml). Not one is referenced by the class body, and
//     the three that would matter - System.Data, System.Xml and System.Configuration - are exactly
//     the couplings this layer forbids. Implicit usings cover what remains.
//   - the five private backing fields at lines 32-36 together with the five Get/Set property
//     blocks at lines 42-81 that wrapped them. Auto-properties express the identical contract.
//   - the parameterless constructor at lines 38-40. Its only statement is "_DefaultCacheTime = 0",
//     which the runtime already guarantees for an Int32 field, so the constructor restated the
//     default rather than establishing it. See DefaultCacheTime below: the zero it produced is a
//     real schema default and survives without any code to assert it.
//
// MIGRATION: TempModuleID (Library/Components/Modules/ModuleDefinitionInfo.vb line 35 for the field
//   and lines 66-73 for the property) is deliberately absent, and must not be added back. It is not
//   a column and it is not this aggregate's identity - it is a parse-time correlation number that
//   exists only until a real ModuleDefID has been assigned. Three independent measurements:
//     1. Zero hits. A case-insensitive search for the name across all eighty-eight
//        Website/Providers/DataProviders/SqlDataProvider/*.SqlDataProvider scripts returns nothing,
//        in any of the four naming forms those scripts use (bare, dbo-qualified, bracketed and
//        database-owner/object-qualifier templated).
//     2. Never persisted. The terminal AddModuleDefinition procedure
//        (03.01.00.SqlDataProvider line 304) takes three parameters and the terminal
//        UpdateModuleDefinition (line 327) takes three, and
//        Library/Components/Modules/ModuleDefinitionController.vb lines 33 and 58 pass exactly
//        those - DesktopModuleID, FriendlyName and DefaultCacheTime, plus ModuleDefID on the
//        update. No data-provider call anywhere accepts TempModuleID.
//     3. Installer-only, and that installer is out of scope. Every remaining reference lives under
//        Library/Components/ResourceInstaller/, a tree this migration excludes:
//        PaDnnAdapter_V2.vb lines 171, 179, 187 and 236, PaDnnAdapter_V2Skin.vb lines 161 and 211,
//        PaDnnAdapter_V3.vb lines 70 and 72, and PaDnnInstallerBase.vb line 364.
//   Its lifetime is visible in that code. While parsing a module manifest,
//   PaDnnAdapter_V2.GetModuleFromNode (line 162) stamps each unsaved definition with a caller-
//   supplied number (line 171) and hands the same number to every control it parses (line 179), and
//   GetModuleControlFromNode stores it in ModuleControlInfo.ModuleDefID (line 236) under the
//   comment "This is a temporary relationship since the ModuleDef has not been saved to the db / it
//   does not have a valid ModuleDefID. Once it is saved then we can update the ModuleControlDef
//   with the correct value." PaDnnInstallerBase.RegisterModules then saves each definition and takes
//   the database-generated key (line 407), and rewrites every control's placeholder through
//   GetModDefID (line 421), which scans the in-memory list for a matching TempModuleID and returns
//   the real ModuleDefID (lines 359-370). The correlation therefore never outlives one installation
//   run. Modelling it would publish request-scoped state on a persistence-shaped type and would
//   invite a caller to mistake it for an identity; the target expresses the same relationship with
//   an object reference, so once ModuleControl instances are attached to ModuleControls below,
//   nothing needs a placeholder number at all.
//
// MIGRATION: nine columns that dbo.ModuleDefinitions once carried are gone from the terminal schema
//   and MUST NOT be recreated on this entity, on its configuration, or on any migration. The
//   02.00.00 upgrade split this table into the host-level package (dbo.DesktopModules) and the
//   definition that a package publishes (this table), and it removed all nine in a single statement
//   at 02.00.00.SqlDataProvider lines 5254-5255:
//   DesktopSrc, MobileSrc, EditSrc, Secure, EditModuleIcon, AdminTabIcon, AdminOrder, Description
//   and IsPremium. Four of the nine were not discarded but relocated, which is why searching for
//   them still finds live data:
//     - Description and IsPremium moved to dbo.DesktopModules. The migration loop at lines
//       5176-5221 inserts one desktop-module row per definition carrying FriendlyName, Description,
//       IsPremium and an IsAdmin derived from whether AdminOrder was null, then back-fills
//       DesktopModuleID here. They are on DesktopModule now; read them through DesktopModule.
//     - AdminTabIcon moved to dbo.Tabs.IconFile (lines 5224-5228) and EditModuleIcon moved to
//       dbo.Modules.IconFile (lines 5231-5235), each matched by name.
//   The remaining five - DesktopSrc, MobileSrc, EditSrc, Secure and AdminOrder - have no successor
//   column anywhere. DesktopSrc, MobileSrc and EditSrc were per-definition Web Forms control paths,
//   superseded by dbo.ModuleControls rows (see ModuleControls below); Secure was a page-level flag
//   whose default constraint went at lines 5246-5247; AdminOrder survives only as the boolean it
//   was converted into on DesktopModule. Re-adding any of the nine would contradict Rule T4, under
//   which the legacy schema is immutable and this table has exactly four columns.
//
// MIGRATION: this entity carries no attribute of any kind and takes no dependency, so nothing here
//   states how it is stored. Table naming, column naming, the key, the unique constraint, the
//   foreign key and the default value are all configured by ModuleDefinitionConfiguration in the
//   Infrastructure layer through the Fluent API; the obligations that configuration inherits are
//   recorded against the members they concern, and the terminal column contract is tabulated below.
//   This file introduces no behavioural divergence of its own beyond the removal of TempModuleID,
//   which is to be recorded in MIGRATION_NOTES.md as an appended entry under the module domain,
//   worded as: "ModuleDefinitionInfo.TempModuleID is not modelled. It is a manifest-parse-time
//   correlation number with no column in dbo.ModuleDefinitions, never passed to any data-provider
//   call, and referenced only by the excluded ResourceInstaller tree; the target expresses the same
//   definition-to-control relationship with an object reference." Append to that document only -
//   mkdocs.yml must not be edited, because its nav entries resolve relative to docs/ and cannot
//   address a repository-root file.

/// <summary>
/// One published definition within an installed module package: the unit a portal actually places
/// on a page, and the unit that owns the controls, permissions and profile properties belonging to
/// that placement.
/// </summary>
/// <remarks>
/// <para>
/// This is the definition-level half of the module model. A <see cref="DesktopModule"/> is the
/// once-per-installation registration of a package; a package publishes one or more definitions,
/// and each definition is what a placed module instance points at. The distinction is the whole
/// subject of the 02.00.00 split annotated above, and it is why this entity is deliberately small:
/// everything descriptive about the package - its description, version, folder, business-controller
/// class and capability bits - belongs to the desktop module, and everything about a particular
/// placement belongs to the module instance.
/// </para>
/// <para>
/// <b>Terminal mapping contract for <c>ModuleDefinitionConfiguration</c>.</b> The legacy schema is
/// immutable for this migration, so the table below is the measured terminal state of
/// <c>dbo.ModuleDefinitions</c> after all eighty-eight upgrade scripts in
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> have been replayed in order - not the
/// baseline, which carries seven columns of which only two survive. Six scripts touch the table and
/// nothing after <c>03.01.01</c> alters it. Every column is bound explicitly with
/// <c>HasColumnName</c>, and the table with <c>ToTable("ModuleDefinitions", "dbo")</c>, because the
/// legacy installation runs with an empty object qualifier and <c>dbo</c> as its database owner.
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Column</term>
///     <description>Terminal declaration, and the script that produced it</description>
///   </listheader>
///   <item>
///     <term>ModuleDefID</term>
///     <description>
///     <c>int IDENTITY(1, 1) NOT NULL</c>, primary key, non-clustered. Created by <c>01.00.00</c>
///     line 66; the <c>PK_ModuleDefinitions</c> constraint follows at line 464. Unlike four other
///     in-scope tables this identity seeds at 1, so no seed value collides with the legacy integer
///     sentinel of -1. Mapped from <see cref="ModuleDefinitionId"/>.
///     </description>
///   </item>
///   <item>
///     <term>FriendlyName</term>
///     <description>
///     <c>nvarchar(128) NOT NULL</c> (<c>01.00.00</c> line 67), and unique: <c>01.00.08</c> line
///     5867 adds <c>CONSTRAINT IX_ModuleDefinitions UNIQUE NONCLUSTERED (FriendlyName)</c>, which
///     no later upgrade script drops. The configuration must reproduce that uniqueness. Note the
///     asymmetry with <see cref="DesktopModule"/>: the equivalent constraint on
///     <c>dbo.DesktopModules</c> was dropped by <c>03.01.00</c> line 34 and replaced with a plain
///     index, so a friendly name is unique among definitions but not among packages.
///     </description>
///   </item>
///   <item>
///     <term>DesktopModuleID</term>
///     <description>
///     <c>int NOT NULL</c>, added by <c>02.00.00</c> line 5174 with a default of <c>0</c> that the
///     same script drops again at lines 5242-5243 once the back-fill has run - so the terminal
///     column is required and has <b>no</b> default, and the configuration must not invent one.
///     Constrained by <c>FK_ModuleDefinitions_DesktopModules</c> at line 5258, declared
///     <c>ON DELETE CASCADE</c>, and indexed by <c>IX_ModuleDefinitions_1</c> at line 5269. Mapped
///     from <see cref="DesktopModuleId"/>.
///     </description>
///   </item>
///   <item>
///     <term>DefaultCacheTime</term>
///     <description>
///     <c>int NOT NULL</c> with a default of <c>0</c>, added by <c>03.01.00</c> line 298 and
///     re-asserted by <c>03.01.01</c>, which drops the default constraint by name lookup (lines
///     991-999), restates the column as <c>NOT NULL</c> at line 1001 and re-creates the default at
///     line 1003. The configuration must reproduce the default so an insert that omits the column
///     still lands on zero rather than on a database error.
///     </description>
///   </item>
/// </list>
/// <para>
/// Four columns, and four persisted properties below - no more. The count is corroborated
/// independently of the schema by the terminal stored procedures: <c>AddModuleDefinition</c>
/// (<c>03.01.00</c> line 304) takes exactly <c>DesktopModuleId</c>, <c>FriendlyName</c> and
/// <c>DefaultCacheTime</c>, which is these columns less the generated identity, and
/// <c>UpdateModuleDefinition</c> (line 327) takes <c>ModuleDefId</c>, <c>FriendlyName</c> and
/// <c>DefaultCacheTime</c> - so the owning package is fixed at insertion and never rewritten.
/// <c>ModuleDefinitionController.vb</c> lines 33 and 58 pass precisely those argument lists.
/// Anything absent from the table above is absent from this entity: no audit columns, since the
/// table declares none, which is also why this type derives from <see cref="Entity{TId}"/> rather
/// than from the auditable base.
/// </para>
/// <para>
/// The type is a persistence-shaped record of state and carries no behaviour. The legacy
/// <c>ModuleDefinitionController</c> held all of it - the two writes, the three reads and the
/// <c>DataCache.ClearModuleCache()</c> call that followed every mutation - and each of those
/// concerns belongs to a repository, an application service or the cache service in the target,
/// never to the entity. Its reflection-driven hydration through <c>CBO.FillObject</c> and
/// <c>CBO.FillCollection</c> is replaced outright by the object-relational mapper's materialiser, so
/// no hydration member appears here either.
/// </para>
/// <para>
/// Absence is expressed with nullable CLR types, never with the legacy sentinel values held in
/// <c>Library/Components/Shared/Null.vb</c>. Nothing on this entity is optional, so no property here
/// is nullable and no sentinel appears anywhere in this file - not even in a comparison. Where the
/// legacy sentinel remains observable in a value, as it does for
/// <see cref="DefaultCacheTime"/>, it survives as an ordinary stored integer that this layer passes
/// through untouched; only the API boundary may restate it as a wire convention.
/// </para>
/// </remarks>
public sealed class ModuleDefinition : Entity<int>
{
    /// <summary>
    /// Gets or sets the installation-wide identity of this module definition.
    /// </summary>
    /// <value>
    /// The value of the <c>ModuleDefID</c> column: a database-generated identity seeded at 1.
    /// </value>
    /// <remarks>
    /// The property keeps the legacy column's meaning while spelling the name in full and the
    /// suffix as <c>Id</c>, which is the casing the rest of the target uses; the column name itself
    /// is unchanged, and <c>ModuleDefinitionConfiguration</c> binds this property to
    /// <c>ModuleDefID</c> explicitly with <c>HasColumnName</c> and names it in its <c>HasKey</c>
    /// call. Because the column is <c>IDENTITY(1, 1)</c>, the configuration marks it generated on
    /// add, and a value assigned before insertion is not honoured.
    /// </remarks>
    public int ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets the value that identifies this entity for the purposes of equality.
    /// </summary>
    /// <value>The value of <see cref="ModuleDefinitionId"/>.</value>
    /// <remarks>
    /// Required by <see cref="Entity{TId}"/> and used for equality alone. It is get-only, carries no
    /// attribute and is mapped to nothing: <c>ModuleDefinitionConfiguration</c> names
    /// <see cref="ModuleDefinitionId"/> in its <c>HasKey</c> call and must additionally exclude this
    /// member from the model with <c>builder.Ignore(...)</c>, or the model builder discovers it by
    /// convention and the provider then fails on a column <c>dbo.ModuleDefinitions</c> does not
    /// have.
    /// </remarks>
    public override int Identity => ModuleDefinitionId;

    /// <summary>
    /// Gets or sets the display name presented to administrators when they choose which definition
    /// to place - such as <c>Announcements</c>.
    /// </summary>
    /// <value>The value of the <c>FriendlyName</c> column. Required, at most 128 characters.</value>
    /// <remarks>
    /// Unique in the terminal schema through <c>IX_ModuleDefinitions</c>, so it is a legitimate
    /// lookup key - and the legacy code used it as one:
    /// <c>ModuleDefinitionController.GetModuleDefinitionByName</c> (line 45) resolves a definition
    /// from its owning package and this name, which is the pairing the installer relies on to decide
    /// between inserting a definition and updating one
    /// (<c>PaDnnInstallerBase.vb</c> lines 403-411).
    /// </remarks>
    public string FriendlyName { get; set; }

    /// <summary>
    /// Gets or sets the identity of the installed package that publishes this definition.
    /// </summary>
    /// <value>
    /// The value of the <c>DesktopModuleID</c> column: the <c>DesktopModuleID</c> of the owning
    /// <see cref="Entities.DesktopModule"/>. Required, with no default in the terminal schema.
    /// </value>
    /// <remarks>
    /// <para>
    /// The foreign key that the 02.00.00 split introduced, and the property that makes this entity
    /// the dependent half of the package-to-definition relationship.
    /// <c>ModuleDefinitionConfiguration</c> binds it to <c>DesktopModuleID</c> with
    /// <c>HasColumnName</c> and uses it as the foreign key behind
    /// <see cref="Entities.DesktopModule"/>, describing the relationship's
    /// <c>ON DELETE CASCADE</c> rule as the existing schema declares it rather than softening it:
    /// deleting a package already removes its definitions in the legacy database.
    /// </para>
    /// <para>
    /// A plain non-nullable <see cref="int"/>, because the column is <c>NOT NULL</c> and every row
    /// has an owner. Its transitional default of <c>0</c> existed only long enough for the 02.00.00
    /// back-fill to run and was dropped by the same script, so nothing may treat zero as "no
    /// package": it is either a real package identity or a row that could not have been inserted.
    /// The legacy writer confirms the requirement - <c>PaDnnInstallerBase.vb</c> line 406 assigns the
    /// package identity immediately before the insert, and <c>UpdateModuleDefinition</c> never
    /// rewrites it.
    /// </para>
    /// </remarks>
    public int DesktopModuleId { get; set; }

    /// <summary>
    /// Gets or sets the number of seconds a placed instance of this definition may serve cached
    /// output by default - the legacy "Cache Time (secs):" setting.
    /// </summary>
    /// <value>
    /// The value of the <c>DefaultCacheTime</c> column. Required, and zero unless a value was
    /// stored, which is the database default rather than a convention this type imposes.
    /// </value>
    /// <remarks>
    /// <para>
    /// This is the definition-level default that governs whether caching applies at all; the timeout
    /// actually in force for one placement is a property of that placement and is deliberately not
    /// duplicated here.
    /// </para>
    /// <para>
    /// The default of zero needs no code. The column is <c>NOT NULL</c> with a database default of
    /// <c>0</c>, and a freshly constructed <see cref="int"/> is already zero, so the two agree
    /// without a constructor, an initialiser or a nullable wrapper - which is exactly why the legacy
    /// constructor that assigned zero explicitly could be deleted rather than translated.
    /// </para>
    /// </remarks>
    // MIGRATION: -1 is a REAL STORED VALUE on this column, not an absent one, so this member stays a
    //   plain non-nullable integer and no sentinel machinery is introduced here or in the mapping.
    //   Measured: Website/admin/Modules/ModuleSettings.ascx.vb lines 138-142 read
    //       If objModuleDef.DefaultCacheTime = Null.NullInteger Then
    //           rowCache.Visible = False
    //       Else
    //           txtCacheTime.Text = objModule.CacheTime.ToString
    //       End If
    //   and Library/Components/Shared/Null.vb lines 41-45 define NullInteger as -1. Because the
    //   column is NOT NULL with a default of 0, a stored -1 cannot be an artefact of a database null
    //   - it was written deliberately, and it means "caching does not apply to this definition; hide
    //   the cache-timeout field". Widening the member to a nullable integer would destroy the
    //   distinction between that value and a genuine 0, and translating -1 to null in a mapper would
    //   silently change what an administrator sees. The domain therefore stores the integer exactly
    //   as the column holds it and interprets nothing.
    //
    // MIGRATION: legacy defect, annotated and deliberately NOT repaired. The legacy screen overloads
    //   a numeric sentinel as a user-interface visibility switch, conflating "no cache timeout" with
    //   "caching does not apply". Behaviour is preserved as measured and no companion boolean is
    //   invented to tidy it up, because inventing one would change an externally observable contract
    //   that existing callers may depend on.
    public int DefaultCacheTime { get; set; }

    /// <summary>
    /// Gets or sets the installed package that publishes this definition.
    /// </summary>
    /// <value>
    /// The <see cref="Entities.DesktopModule"/> whose <c>DesktopModuleID</c> equals
    /// <see cref="DesktopModuleId"/>. Required: every definition has exactly one owning package.
    /// </value>
    /// <remarks>
    /// <para>
    /// The inverse of <see cref="Entities.DesktopModule.ModuleDefinitions"/>, and the replacement for
    /// the flattened join that the legacy model used instead of a reference. Reach the package's
    /// descriptive members - description, version, folder name, module name, business-controller
    /// class and the capability bits - through this property; none of them is duplicated on this
    /// entity, and the two the 02.00.00 split moved out of this table (<c>Description</c> and
    /// <c>IsPremium</c>) are found here.
    /// </para>
    /// <para>
    /// Not initialised and not nullable, which is the deliberate contract for a required reference
    /// navigation: it is populated either by the object-relational mapper when the relationship is
    /// loaded or by the caller that builds a new definition, and a self-assigned empty instance
    /// would be a lie about which package owns this row. It is consequently unsafe to dereference on
    /// an entity that was fetched without the relationship included, exactly as for any other
    /// reference navigation.
    /// </para>
    /// </remarks>
    public DesktopModule DesktopModule { get; set; }

    /// <summary>
    /// Gets or sets the user-interface controls this definition publishes.
    /// </summary>
    /// <value>
    /// The control rows whose <c>ModuleDefID</c> refers to this row. Initialised to an empty
    /// collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// A real relationship rather than an inferred one: <c>ModuleControls.ModuleDefID</c> is
    /// constrained by <c>FK_ModuleControls_ModuleDefinitions</c>, declared
    /// <c>ON DELETE CASCADE</c> in <c>02.00.00</c> line 5026, and the same script constrains the
    /// triple <c>(ModuleDefID, ControlKey, ControlSrc)</c> to be unique through
    /// <c>IX_ModuleControls</c> at line 5017 - so one definition never publishes the same key and
    /// source twice.
    /// </para>
    /// <para>
    /// These rows are the successor to the three per-definition control paths that the 02.00.00
    /// split removed from this table (<c>DesktopSrc</c>, <c>MobileSrc</c> and <c>EditSrc</c>): what
    /// was a fixed trio of columns became an open-ended set of typed control rows. The foreign-key
    /// column is itself nullable, so host-level controls that belong to no definition exist and
    /// simply never appear in any definition's collection.
    /// </para>
    /// </remarks>
    public ICollection<ModuleControl> ModuleControls { get; set; } = [];

    /// <summary>
    /// Gets or sets the placed module instances created from this definition.
    /// </summary>
    /// <value>
    /// The module rows whose <c>ModuleDefID</c> refers to this row. Initialised to an empty
    /// collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// The oldest relationship on this table and the reason it exists:
    /// <c>FK_Modules_ModuleDefinitions</c> is declared in the baseline schema at <c>01.00.00</c>
    /// line 674, likewise <c>ON DELETE CASCADE</c>. A definition describes what may be placed; each
    /// row here is one actual placement, carrying its own title, permissions and configured cache
    /// timeout. The cascade is a property of the existing schema and the configuration must describe
    /// it as it stands: removing a definition already removes every instance placed from it.
    /// </remarks>
    public ICollection<Module> Modules { get; set; } = [];

    /// <summary>
    /// Gets or sets the permission types this definition defines.
    /// </summary>
    /// <value>
    /// The permission rows whose <c>ModuleDefID</c> refers to this row. Initialised to an empty
    /// collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// The catalogue of permissions a definition understands - the named actions, such as view and
    /// edit, that a grant on a placed instance can refer to. <c>dbo.Permission</c> declares
    /// <c>ModuleDefID</c> as <c>int NOT NULL</c> at <c>02.02.00</c> line 687, so every permission
    /// type belongs to exactly one definition.
    /// </para>
    /// <para>
    /// Unlike <see cref="ModuleControls"/> and <see cref="Modules"/>, this relationship is
    /// <b>not</b> backed by a foreign key: no <c>FK_..._ModuleDefinitions</c> constraint over
    /// <c>dbo.Permission</c> exists anywhere in the upgrade chain. The legacy code performed the
    /// cascade itself - the terminal <c>DeleteModuleDefinition</c> procedure
    /// (<c>04.06.00</c> line 507) runs <c>DELETE FROM Permission WHERE moduledefid = @ModuleDefId</c>
    /// under the comment "delete custom permissions" before deleting the definition row. The
    /// configuration must therefore declare this relationship explicitly rather than rely on
    /// discovery from a constraint that is not there, and, because the database will not cascade,
    /// deleting a definition remains an operation that has to remove these rows deliberately.
    /// </para>
    /// </remarks>
    public ICollection<Permission> Permissions { get; set; } = [];

    /// <summary>
    /// Gets or sets the user-profile properties this definition contributes.
    /// </summary>
    /// <value>
    /// The profile-property definition rows whose <c>ModuleDefID</c> refers to this row. Initialised
    /// to an empty collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// The mechanism by which a module definition extends a portal's user profile with properties of
    /// its own. <c>dbo.ProfilePropertyDefinition</c> declares <c>ModuleDefID</c> as
    /// <c>int NULL</c> (<c>04.00.04</c> line 1111, first created by <c>03.02.03</c> line 1066) and
    /// includes it in the unique index over <c>(PortalID, ModuleDefID, PropertyName)</c> at line
    /// 1127, so a property name is unique per portal per contributing definition.
    /// </para>
    /// <para>
    /// Two consequences follow from that nullable column, and the configuration must honour both:
    /// the relationship is optional on the dependent side, so the portal-wide profile properties
    /// that belong to no definition carry a null and simply never appear in any definition's
    /// collection; and, as with <see cref="Permissions"/>, no foreign key enforces it, so the
    /// relationship has to be declared explicitly and no database cascade will remove these rows.
    /// </para>
    /// </remarks>
    public ICollection<ProfilePropertyDefinition> ProfilePropertyDefinitions { get; set; } = [];
}
