using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: legacy type DotNetNuke.Entities.Modules.DesktopModuleInfo
//   (Library/Components/Modules/DesktopModuleInfo.vb, 254 lines) becomes this entity. The legacy
//   "...Info" suffix existed only to distinguish a data-carrying class from its static
//   "...Controller" companion; the target draws that distinction with the layer boundary instead,
//   so the suffix carries no information and is dropped.
//
// MIGRATION: constructs deleted rather than translated, each measured unused or unreachable:
//   - the six Imports at lines 21-26 (System, System.Configuration, System.Data,
//     System.Globalization, System.IO and System.Xml). Not one is referenced by the class body,
//     and the three that would matter - System.Data, System.Xml and System.Configuration - are
//     exactly the couplings this layer forbids.
//   - the thirteen private backing fields at lines 40-52 together with the thirteen Get/Set
//     property blocks that wrapped them. Auto-properties express the identical contract.
//   - the empty parameterless constructor at lines 58-59. The compiler supplies it.
//   - the nested enumeration DesktopModuleSupportedFeature at lines 30-34
//     (IsPortable = 1, IsSearchable = 2, IsUpgradeable = 4), which is deliberately NOT recreated as
//     a type. See the feature surface further down for the reasoning and its replacement.
//   - the four private bit-manipulation helpers at lines 213-246: ClearFeature (AND with the
//     complement), GetFeature (the guarded mask test), SetFeature (OR with the mask) and
//     UpdateFeature (the read-modify-write that drove the two above).
//
// MIGRATION: this entity carries no attribute of any kind and takes no dependency, so nothing here
//   states how it is stored. Table and column naming, types, the key, the two unique constraints
//   and both relationships are configured by DesktopModuleConfiguration in the Infrastructure layer
//   through the Fluent API; the obligations that configuration inherits are recorded against the
//   members they concern. The one deliberate behavioural divergence introduced by this file - the
//   removal of the three feature setters - is to be recorded in MIGRATION_NOTES.md as an appended
//   entry under the module domain, worded as: "DesktopModule.IsPortable, IsSearchable and
//   IsUpgradeable are read-only projections over the persisted SupportedFeatures bit field. The
//   legacy read-modify-write setters on DesktopModuleInfo were removed; their only caller in the
//   entire legacy tree is EventMessageProcessor.UpdateSupportedFeatures, which belongs to the
//   excluded module-loader infrastructure." Append to that document only - mkdocs.yml must not be
//   edited, because its nav entries resolve relative to docs/ and cannot address a
//   repository-root file.

/// <summary>
/// A module package installed on the DotNetNuke host: the once-per-installation registration of a
/// deployable module, distinct from the per-page instances placed from it.
/// </summary>
/// <remarks>
/// <para>
/// This is the host-level half of the module model. One desktop module is registered per installed
/// package and describes what the package is and what it can do; the page-level placements that
/// portals actually render are separate aggregates. Two relationships radiate from here, and both
/// are backed by a real foreign key rather than inferred: the module definitions the package
/// publishes, and the per-portal grants that decide which portals may use it.
/// </para>
/// <para>
/// <b>Terminal mapping contract for <c>DesktopModuleConfiguration</c>.</b> The legacy schema is
/// immutable for this migration, so the table below is the measured terminal state of
/// <c>dbo.DesktopModules</c> after all eighty-eight upgrade scripts in
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> have been replayed in order - not the
/// baseline, which carries only the first six columns. Six scripts touch the table and nothing
/// after <c>04.05.00</c> alters it. Every column is bound explicitly with
/// <c>HasColumnName</c>, and the table with <c>ToTable("DesktopModules", "dbo")</c>, because the
/// legacy installation runs with an empty object qualifier and <c>dbo</c> as its database owner.
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Column</term>
///     <description>Terminal declaration, and the script that produced it</description>
///   </listheader>
///   <item>
///     <term>DesktopModuleID</term>
///     <description>
///     <c>int IDENTITY(1, 1) NOT NULL</c>, primary key, clustered. Created by
///     <c>02.00.00</c> line 5141; the <c>PK_DesktopModules</c> constraint follows at line 5151.
///     Unlike four other in-scope tables this identity seeds at 1, so no seed value collides with
///     the legacy integer sentinel.
///     </description>
///   </item>
///   <item>
///     <term>FriendlyName</term>
///     <description>
///     <c>nvarchar(128) NOT NULL</c> (<c>02.00.00</c> line 5142). Originally unique through
///     <c>IX_DesktopModules</c> (line 5158), but that constraint is dropped by <c>03.01.00</c>
///     line 34 and replaced at line 37 by the non-unique index
///     <c>IX_DesktopModules_FriendlyName</c>. The terminal schema therefore does <b>not</b>
///     constrain this column to be unique, and the configuration must not re-impose it.
///     </description>
///   </item>
///   <item>
///     <term>Description</term>
///     <description><c>nvarchar(2000) NULL</c> (<c>02.00.00</c> line 5143).</description>
///   </item>
///   <item>
///     <term>Version</term>
///     <description><c>nvarchar(8) NULL</c> (<c>02.00.00</c> line 5144).</description>
///   </item>
///   <item>
///     <term>IsPremium</term>
///     <description><c>bit NOT NULL</c> (<c>02.00.00</c> line 5145).</description>
///   </item>
///   <item>
///     <term>IsAdmin</term>
///     <description><c>bit NOT NULL</c> (<c>02.00.00</c> line 5146).</description>
///   </item>
///   <item>
///     <term>BusinessControllerClass</term>
///     <description>
///     <c>nvarchar(200) NULL</c>, added by <c>02.02.02</c> line 116. Nullable by omission: the
///     script states no nullability, so the column takes the default.
///     </description>
///   </item>
///   <item>
///     <term>FolderName</term>
///     <description>
///     <c>nvarchar(128) NOT NULL</c>. Added as nullable by <c>03.01.00</c> line 12, back-filled
///     from <c>FriendlyName</c> at line 17, then promoted to <c>NOT NULL</c> at line 22.
///     </description>
///   </item>
///   <item>
///     <term>ModuleName</term>
///     <description>
///     <c>nvarchar(128) NOT NULL</c>, and the only uniquely constrained column in the terminal
///     schema: added as nullable by <c>03.01.00</c> line 13, back-filled at line 17, promoted to
///     <c>NOT NULL</c> at line 26, then constrained by
///     <c>IX_DesktopModules_ModuleName UNIQUE NONCLUSTERED</c> at line 30.
///     </description>
///   </item>
///   <item>
///     <term>SupportedFeatures</term>
///     <description>
///     <c>int NOT NULL</c> with a default of <c>0</c>, added by <c>03.01.00</c> line 14 and
///     re-asserted by <c>03.01.01</c> lines 912 and 914 after that script drops and re-creates the
///     default constraint. The configuration must reproduce the default so an insert that omits
///     the column still lands on "no capabilities" rather than on a database error.
///     </description>
///   </item>
///   <item>
///     <term>CompatibleVersions</term>
///     <description><c>nvarchar(500) NULL</c>, added by <c>04.03.06</c> line 54.</description>
///   </item>
///   <item>
///     <term>Dependencies</term>
///     <description><c>nvarchar(400) NULL</c>, added by <c>04.05.00</c> line 966.</description>
///   </item>
///   <item>
///     <term>Permissions</term>
///     <description><c>nvarchar(400) NULL</c>, added by <c>04.05.00</c> line 971.</description>
///   </item>
/// </list>
/// <para>
/// Thirteen columns, and thirteen persisted properties below - no more. The count is corroborated
/// independently of the schema by the terminal stored procedure <c>AddDesktopModule</c> in
/// <c>04.05.00</c>, whose twelve parameters are exactly these columns less the generated identity,
/// and by <c>DesktopModuleController.vb</c> lines 33 and 75, which pass precisely that argument
/// list. Anything absent from the table above is absent from this entity: no audit columns, since
/// the table declares none, which is also why this type derives from
/// <see cref="Entity{TId}"/> rather than from the auditable base.
/// </para>
/// <para>
/// Absence is expressed with nullable CLR types, never with the legacy sentinel values held in
/// <c>Library/Components/Shared/Null.vb</c>. Those sentinels survive at the API boundary, where a
/// wire contract may be externally observable, and nowhere else. The single trace of them in this
/// file is the guard inside the three computed capability properties, which is preserved because it
/// is observable legacy behaviour rather than a storage convention.
/// </para>
/// </remarks>
public sealed class DesktopModule : Entity<int>
{
    // MIGRATION: the legacy nested enumeration DesktopModuleSupportedFeature
    //   (Library/Components/Modules/DesktopModuleInfo.vb lines 30-34) is intentionally not
    //   recreated. It was a flags enumeration in everything but declaration - it carried no
    //   FlagsAttribute, and its three members were only ever used as bit masks against one integer
    //   column - so promoting it to a domain type would publish a persistence detail as vocabulary
    //   and invite callers to store or transport a bit field. The masks live here instead, private
    //   and named, and the capability questions callers actually asked are answered by the three
    //   boolean properties further down.

    /// <summary>
    /// Bit that records support for content export and import, matching the legacy
    /// <c>DesktopModuleSupportedFeature.IsPortable</c> member value.
    /// </summary>
    private const int PortableFeatureMask = 1;

    /// <summary>
    /// Bit that records support for content indexing, matching the legacy
    /// <c>DesktopModuleSupportedFeature.IsSearchable</c> member value.
    /// </summary>
    private const int SearchableFeatureMask = 2;

    /// <summary>
    /// Bit that records support for version-driven upgrade handling, matching the legacy
    /// <c>DesktopModuleSupportedFeature.IsUpgradeable</c> member value.
    /// </summary>
    private const int UpgradeableFeatureMask = 4;

    /// <summary>
    /// Gets or sets the host-wide identity of this desktop module.
    /// </summary>
    /// <value>
    /// The value of the <c>DesktopModuleID</c> column: a database-generated identity seeded at 1.
    /// </value>
    /// <remarks>
    /// The property keeps the legacy column's meaning while spelling the suffix as <c>Id</c>, which
    /// is the casing the rest of the target uses; the column name itself is unchanged and is bound
    /// explicitly by the entity configuration. Because the column is
    /// <c>IDENTITY(1, 1)</c>, the configuration marks it generated on add, and a value assigned
    /// before insertion is not honoured.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    /// <summary>
    /// Gets the value that identifies this entity for the purposes of equality.
    /// </summary>
    /// <value>The value of <see cref="DesktopModuleId"/>.</value>
    /// <remarks>
    /// Required by <see cref="Entity{TId}"/> and used for equality alone. It is get-only, carries no
    /// attribute and is mapped to nothing: the entity configuration names
    /// <see cref="DesktopModuleId"/> in its <c>HasKey</c> call and must additionally exclude this
    /// member from the model with <c>builder.Ignore(...)</c>, exactly as it must for the three
    /// computed capability properties below.
    /// </remarks>
    public override int Identity => DesktopModuleId;

    /// <summary>
    /// Gets or sets the display name presented to administrators, such as
    /// <c>Announcements</c>.
    /// </summary>
    /// <value>The value of the <c>FriendlyName</c> column. Required, at most 128 characters.</value>
    /// <remarks>
    /// Not unique in the terminal schema. The unique constraint the baseline placed on this column
    /// was dropped in favour of a plain index, and <see cref="ModuleName"/> carries uniqueness
    /// instead, so this name may legitimately repeat and must not be treated as a lookup key.
    /// </remarks>
    public string FriendlyName { get; set; }

    /// <summary>
    /// Gets or sets the administrator-facing summary of what the module does.
    /// </summary>
    /// <value>
    /// The value of the <c>Description</c> column, at most 2000 characters, or
    /// <see langword="null"/> when the column holds no value.
    /// </value>
    /// <remarks>
    /// Genuinely optional in the schema. Legacy reads of this column passed through the sentinel
    /// helper, which converted a database null into the empty string, so legacy callers could not
    /// distinguish "no description" from "an empty description"; the nullable type here restores
    /// that distinction, and any contract that must keep the legacy appearance re-imposes the empty
    /// string in the data-transfer layer rather than here.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the installed package version, in the legacy <c>NN.NN.NN</c> form - for
    /// example <c>03.01.00</c>.
    /// </summary>
    /// <value>
    /// The value of the <c>Version</c> column, at most 8 characters, or <see langword="null"/> when
    /// the column holds no value.
    /// </value>
    /// <remarks>
    /// Held as text, not as a structured version, because the column is <c>nvarchar(8)</c> and the
    /// legacy upgrade scripts write and compare it as the zero-padded string above - see the
    /// <c>Version = '03.01.00'</c> assignments in <c>03.01.00</c> from line 40 onwards. Parsing it
    /// into a version type here would change ordering semantics for values the schema permits but
    /// that form does not describe.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the module is premium, meaning a portal must be
    /// granted it explicitly rather than receiving it by default.
    /// </summary>
    /// <value>The value of the <c>IsPremium</c> column. Required.</value>
    /// <remarks>
    /// This flag is what gives <see cref="PortalDesktopModules"/> its purpose: a premium module is
    /// usable by a portal only where a grant row exists for that pairing.
    /// </remarks>
    public bool IsPremium { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the module belongs to the portal administration
    /// surface rather than to ordinary page content.
    /// </summary>
    /// <value>The value of the <c>IsAdmin</c> column. Required.</value>
    /// <remarks>
    /// Administrative modules are reached through the administration areas of a portal, so a
    /// definition picker offered for ordinary content normally filters them out.
    /// </remarks>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Gets or sets the assembly-qualified name of the type that implements the module's own
    /// behaviour, such as <c>DotNetNuke.Modules.Announcements.AnnouncementsController,
    /// DotNetNuke.Modules.Announcements</c>.
    /// </summary>
    /// <value>
    /// The value of the <c>BusinessControllerClass</c> column, at most 200 characters, or
    /// <see langword="null"/> when the package publishes no such type.
    /// </value>
    /// <remarks>
    /// <para>
    /// Retained because it is a persisted column of the immutable schema and the legacy export and
    /// import guards read it - <c>ModuleController.vb</c> line 229 and
    /// <c>Website/admin/Modules/Export.ascx.vb</c> line 150 both test that it is non-empty and that
    /// <see cref="IsPortable"/> holds before doing any work.
    /// </para>
    /// <para>
    /// What it must never be used for is activation. The legacy code passed this string to a
    /// reflection-based activator, which probed the deployed assemblies for whatever type the value
    /// named; the target resolves module behaviour from a closed, dependency-injected set through a
    /// factory in the Application layer. This property is inert data here: the Domain layer neither
    /// loads a type nor knows how one would be loaded.
    /// </para>
    /// </remarks>
    public string? BusinessControllerClass { get; set; }

    /// <summary>
    /// Gets or sets the name of the module's own directory within the legacy module tree, such as
    /// <c>Announcements</c>.
    /// </summary>
    /// <value>The value of the <c>FolderName</c> column. Required, at most 128 characters.</value>
    /// <remarks>
    /// Required by the terminal schema even though the physical directory it names belongs to a Web
    /// Forms deployment layout that the target does not serve, which is why the column is preserved
    /// as data while nothing in the target composes a path from it. Its history explains its
    /// content: the column arrived nullable, was back-filled from <see cref="FriendlyName"/>, and
    /// was only then promoted to required, so on an upgraded installation many rows hold a folder
    /// name that is simply the display name.
    /// </remarks>
    public string FolderName { get; set; }

    /// <summary>
    /// Gets or sets the stable, installation-wide key for the module, such as
    /// <c>DNN_Announcements</c>.
    /// </summary>
    /// <value>The value of the <c>ModuleName</c> column. Required, at most 128 characters.</value>
    /// <remarks>
    /// The one column the terminal schema constrains to be unique, and therefore the natural key
    /// for a lookup by name; the legacy provider exposes a dedicated
    /// <c>GetDesktopModuleByModuleName</c> query for exactly that. Like
    /// <see cref="FolderName"/> it was back-filled from <see cref="FriendlyName"/> before being
    /// promoted to required, and the same upgrade script then rewrote the values of the modules it
    /// shipped to the prefixed form above.
    /// </remarks>
    public string ModuleName { get; set; }

    /// <summary>
    /// Gets or sets the bit field recording which optional capabilities the module's behaviour type
    /// implements.
    /// </summary>
    /// <value>
    /// The value of the <c>SupportedFeatures</c> column: required, defaulting to <c>0</c>, meaning
    /// no optional capability.
    /// </value>
    /// <remarks>
    /// <para>
    /// This is the single persisted primitive behind <see cref="IsPortable"/>,
    /// <see cref="IsSearchable"/> and <see cref="IsUpgradeable"/>, and the only writable member of
    /// that group. Callers that need to change a module's capabilities compose the whole value and
    /// assign it once - the legacy sequence, at
    /// <c>Library/Components/Modules/EventMessageProcessor.vb</c> lines 87 to 92, did the same thing
    /// in four statements by zeroing the field and then setting three flags one at a time.
    /// </para>
    /// <para>
    /// Values beyond the three documented bits are neither validated nor stripped. The column is a
    /// plain <c>int</c>, the legacy code never constrained it, and silently normalising a value the
    /// database accepts would make a read followed by a write lose information that some other
    /// installation may depend on.
    /// </para>
    /// </remarks>
    public int SupportedFeatures { get; set; }

    /// <summary>
    /// Gets or sets the range of host versions the package declares itself compatible with.
    /// </summary>
    /// <value>
    /// The value of the <c>CompatibleVersions</c> column, at most 500 characters, or
    /// <see langword="null"/> when the package declares no range.
    /// </value>
    /// <remarks>
    /// One of three installer manifest fields carried on this table - with
    /// <see cref="Dependencies"/> and <see cref="Permissions"/> - and, like them, preserved as
    /// opaque text. The installer subsystem that produced and interpreted these values is out of
    /// scope, so the target reads and writes them faithfully without ascribing structure to them.
    /// </remarks>
    public string? CompatibleVersions { get; set; }

    /// <summary>
    /// Gets or sets the external dependencies the package declares it requires.
    /// </summary>
    /// <value>
    /// The value of the <c>Dependencies</c> column, at most 400 characters, or
    /// <see langword="null"/> when the package declares none.
    /// </value>
    /// <remarks>
    /// Installer manifest metadata, preserved as opaque text for the reason given on
    /// <see cref="CompatibleVersions"/>.
    /// </remarks>
    public string? Dependencies { get; set; }

    /// <summary>
    /// Gets or sets the host permissions the package declares it requires in order to run.
    /// </summary>
    /// <value>
    /// The value of the <c>Permissions</c> column, at most 400 characters, or
    /// <see langword="null"/> when the package declares none.
    /// </value>
    /// <remarks>
    /// Installer manifest metadata, preserved as opaque text for the reason given on
    /// <see cref="CompatibleVersions"/>. Despite the name it has nothing to do with the portal
    /// permission model: it is a code-access declaration made by the package about itself, not a
    /// grant of access to a user or role.
    /// </remarks>
    public string? Permissions { get; set; }

    // MIGRATION: the three properties below are computed, read-only projections over
    //   SupportedFeatures. Four things about them are deliberate.
    //
    //   1. The setters are gone. On the legacy class each of the three
    //      (Library/Components/Modules/DesktopModuleInfo.vb lines 155-180) was read/write, and each
    //      setter called UpdateFeature (line 238), which read the shared bit field, altered one bit
    //      and wrote the whole value back. Three read-modify-write facades over one field is a
    //      lost-update hazard: setting two capabilities from separately-read copies silently
    //      discards one. The target persists the one primitive, SupportedFeatures, and lets callers
    //      compose it - which is what the legacy code already did in practice, since the only
    //      caller of those setters in the entire legacy tree is
    //      EventMessageProcessor.UpdateSupportedFeatures
    //      (Library/Components/Modules/EventMessageProcessor.vb lines 81-96), a member of the
    //      excluded module-loader infrastructure whose reflection-based activation is replaced by a
    //      dependency-injected factory. Every surviving in-scope use is a read:
    //      ModuleController.vb lines 229 and 429, Website/admin/Modules/Export.ascx.vb line 150 and
    //      Website/admin/Modules/Import.ascx.vb line 177. The legacy codebase itself sets the
    //      precedent - the equivalent trio on Library/Components/Modules/ModuleInfo.vb lines 608 to
    //      624 is already declared ReadOnly.
    //
    //   2. None of the three is a column, so DesktopModuleConfiguration MUST call
    //      builder.Ignore(...) for each of IsPortable, IsSearchable and IsUpgradeable, alongside the
    //      Ignore it owes the inherited Identity member. Without those calls the model builder
    //      discovers three properties by convention and the provider then fails on columns that
    //      dbo.DesktopModules does not have. They are read-only, so no persistence concern is
    //      served by mapping them: SupportedFeatures already carries the whole state.
    //
    //   3. The mapping attribute [NotMapped] is forbidden here, and so is every other persistence
    //      or serialisation attribute. Domain declares no package reference at all - see the comment
    //      in DnnMigration.Domain.csproj - so the attribute is not even resolvable, and that is by
    //      design rather than by accident: the layer stays persistence-agnostic, and an entity that
    //      annotated itself for one mapper would be describing infrastructure it must not know
    //      about. The exclusion therefore belongs in the Fluent configuration, which is the layer
    //      that owns the model.
    //
    //   4. The guard is preserved verbatim in meaning. The legacy reader
    //      (DesktopModuleInfo.vb lines 220-229) is
    //          If SupportedFeatures > Null.NullInteger AndAlso (SupportedFeatures And Feature) = Feature
    //      and Library/Components/Shared/Null.vb lines 41-45 define NullInteger as -1, so a
    //      sentinel-valued or otherwise negative bit field reports every capability as false - even
    //      where the two's-complement bit pattern would satisfy the mask, as -1 does for all three.
    //      That measured behaviour, not the bare mask test, is what a caller observes, so
    //      "SupportedFeatures > -1" is written out literally in each expression below. It is
    //      arithmetically the same as ">= 0" for a non-nullable int; the legacy spelling is kept so
    //      the check remains recognisable against the source it came from. This is the only trace of
    //      the legacy sentinel table in this file, and it survives as behaviour rather than as a
    //      storage convention: no property here uses a sentinel to mean "absent".

    /// <summary>
    /// Gets a value indicating whether the module supports content export and import.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="SupportedFeatures"/> is not negative and carries the
    /// portability bit; otherwise <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// Derived, not stored, and deliberately read-only: assign <see cref="SupportedFeatures"/> to
    /// change it. The legacy export and import screens paired this test with a non-empty
    /// <see cref="BusinessControllerClass"/> before offering the action, so a consumer restoring
    /// that affordance needs both.
    /// </remarks>
    public bool IsPortable =>
        SupportedFeatures > -1 && (SupportedFeatures & PortableFeatureMask) == PortableFeatureMask;

    /// <summary>
    /// Gets a value indicating whether the module supports having its content indexed for search.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="SupportedFeatures"/> is not negative and carries the
    /// searchability bit; otherwise <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// Derived, not stored, and deliberately read-only: assign <see cref="SupportedFeatures"/> to
    /// change it. The search subsystem itself is out of scope, so nothing in the target acts on this
    /// value; it is exposed because the bit is persisted and an administrator reading a module's
    /// capabilities expects to see it reported exactly as the legacy screens reported it.
    /// </remarks>
    public bool IsSearchable =>
        SupportedFeatures > -1 && (SupportedFeatures & SearchableFeatureMask) == SearchableFeatureMask;

    /// <summary>
    /// Gets a value indicating whether the module supports version-driven upgrade handling.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="SupportedFeatures"/> is not negative and carries the
    /// upgradeability bit; otherwise <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// Derived, not stored, and deliberately read-only: assign <see cref="SupportedFeatures"/> to
    /// change it. The legacy upgrade subsystem is out of scope, so, as with
    /// <see cref="IsSearchable"/>, this reports a persisted capability rather than driving one.
    /// </remarks>
    public bool IsUpgradeable =>
        SupportedFeatures > -1 && (SupportedFeatures & UpgradeableFeatureMask) == UpgradeableFeatureMask;

    /// <summary>
    /// Gets or sets the module definitions this package publishes.
    /// </summary>
    /// <value>
    /// The definitions whose <c>DesktopModuleID</c> refers to this row. Initialised to an empty
    /// collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// A real relationship rather than an inferred one: <c>ModuleDefinitions.DesktopModuleID</c> is
    /// constrained by <c>FK_ModuleDefinitions_DesktopModules</c>, declared
    /// <c>ON DELETE CASCADE</c> in <c>02.00.00</c> line 5258. The cascade is a property of the
    /// existing schema and the configuration must describe it as it stands rather than soften it,
    /// because deleting a desktop module already removes its definitions in the legacy database.
    /// </remarks>
    public ICollection<ModuleDefinition> ModuleDefinitions { get; set; } = [];

    /// <summary>
    /// Gets or sets the per-portal grants that permit portals to use this module.
    /// </summary>
    /// <value>
    /// The grant rows whose <c>DesktopModuleID</c> refers to this row. Initialised to an empty
    /// collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// The mechanism behind <see cref="IsPremium"/>: <c>PortalDesktopModules</c> pairs a portal with
    /// a desktop module, is unique on that pair through <c>IX_PortalDesktopModules</c>
    /// (<c>02.02.02</c> line 3044), and refers here through
    /// <c>FK_PortalDesktopModules_DesktopModules</c>, likewise <c>ON DELETE CASCADE</c>
    /// (<c>02.02.02</c> line 3053). Because the pairing is unique, a portal appears at most once in
    /// this collection.
    /// </remarks>
    public ICollection<PortalDesktopModule> PortalDesktopModules { get; set; } = [];
}
