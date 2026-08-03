using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: this type models the terminal dbo.Tabs BASE TABLE, not the legacy TabInfo class and not
// the vw_Tabs view. Library/Components/Tabs/TabInfo.vb declares thirty-six public members, of which
// only twenty-two are persisted columns. The other fourteen are accounted for individually below and
// none of them produces a member here.
//
// MIGRATION: the terminal column set was reconstructed by replaying the upgrade chain rather than
// read from any single script, because the chain is destructive. The baseline CREATE TABLE at
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider line 139 declares ten
// columns; eighteen more are added across 01.00.04, 01.00.05, 01.00.06, 01.00.10, 02.00.00,
// 02.02.00, 02.02.02, 03.01.01 and 04.05.04; and six are dropped again - LeftPaneWidth,
// RightPaneWidth, MobileTabName and ShowMobile at 02.00.00 lines 4735-4748, then AdministratorRoles
// and AuthorizedRoles at 03.00.01 lines 1407-1411. Ten plus eighteen less six is the twenty-two
// scalars declared here, a count confirmed independently by the select list of the terminal vw_Tabs
// at 04.05.04 lines 52-87.
//
// MIGRATION: TabID is declared IDENTITY(0, 1) at 01.00.00.SqlDataProvider line 140, so ZERO IS A
// REAL, PERSISTED PAGE IDENTITY - the first row ever inserted owns it. TabId must never be read as
// "absent", "transient", "unsaved" or "not set", and no member may be added that draws such a
// conclusion from its value. Whether the row exists is declared by the persistence layer through
// Entity<int>.MarkIdentityPersisted and read back through Entity<int>.IdentityIsPersisted; it is
// never deduced here.
//
// MIGRATION: the legacy Null sentinels are not ported. Library/Components/Shared/Null.vb lines 36-85
// encode absence as -1 for an integer, Date.MinValue for a date and THE EMPTY STRING for text, and
// the TabInfo constructor at lines 86-105 seeds exactly those values into every SQL-nullable member.
// Here absence is a null in a nullable CLR type, and no scalar carries a sentinel initialiser.
// PortalId and ParentId in particular are int?: null must never be collapsed back to -1 anywhere in
// the domain, and -1 arriving from a legacy payload is a portal key, not an absence.
//
// MIGRATION: a domain entity carries no attribute of any kind. The legacy type is decorated
// <XmlRoot("tab")> with an <XmlElement> or <XmlIgnore> on all thirty-six members; the wire contract
// belongs to the DTOs at the API boundary and the mapping belongs to the Infrastructure entity
// configuration, so neither serialisation nor validation nor persistence metadata appears here.
//
// MIGRATION: Implements IPropertyAccess is dropped with the excluded token-replacement subsystem, so
// the name-keyed GetProperty accessor at TabInfo.vb line 524 and the Cacheability member at line 605
// have no counterpart. Clone at line 470 is dropped as well: copying is the caller's concern and a
// hand-written clone silently drifts from the property set it copies.
//
// MIGRATION: a domain entity performs no I/O, which is what removes the four computed legacy
// members. TabType (line 406) and FullUrl (line 412) resolve navigation URLs; IsAdminTab (line 435)
// is the sharpest case - its getter reaches PortalController.GetCurrentPortalSettings, then
// DataCache.GetPersistentCacheItem, then PortalController.GetPortal, so reading a property issues a
// database query. Whether a page is the administration page is answered by comparing TabId and
// ParentId against the owning portal's AdminTabId in the layer that already holds the portal.
//
// MIGRATION: the six members the legacy class comments as "properties loaded in PortalSettings"
// (lines 347-404) are presentation state and are absent: SkinPath, ContainerPath, BreadCrumbs,
// Panes, Modules and IsSuperTab. The first two belong to the excluded skinning subsystem, the next
// three are untyped ArrayLists filled during page rendering, and IsSuperTab is derived - its getter
// at line 397 returns PortalID = Null.NullInteger, a sentinel comparison this model rejects. A host
// page is expressed by PortalId being null.
//
// MIGRATION: HasChildren (line 291) is deliberately not a member. It is an EXISTS projection in the
// terminal view, "CASE WHEN EXISTS (SELECT 1 FROM Tabs T2 WHERE T2.ParentId = T.TabId) THEN 'true'
// ELSE 'false' END AS 'HasChildren'" at 04.05.04.SqlDataProvider line 83, so it is a question about
// OTHER rows and has no column to bind to. Consumers answer it from the Children navigation when the
// tree is loaded, or from a grouped query when it is not, and carry the answer on a DTO.
//
// MIGRATION: AuthorizedRoles (line 327) and AdministratorRoles (line 336) have no member because
// their columns no longer exist - both were dropped at 03.00.01.SqlDataProvider lines 1407-1411 when
// the semicolon-delimited role strings were superseded by rows in dbo.TabPermission. Page authority
// is read from TabPermissions, never from a delimited string.

/// <summary>
/// A page within a portal's navigation hierarchy: the DotNetNuke "tab" abstraction that module
/// placements and page-level permissions are keyed by.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>DotNetNuke.Entities.Tabs.TabInfo</c>
/// (<c>Library/Components/Tabs/TabInfo.vb</c>) with a persistence POCO over the terminal
/// <c>dbo.Tabs</c> table. Every member below is one of the twenty-two terminal columns, one of the
/// two optional references, or one of the three inverse collections; there is no computed member, no
/// projection, no presentation state and no behaviour.
/// </para>
/// <para>
/// The hierarchy is expressed by <see cref="ParentId"/> alone. <see cref="Level"/> and
/// <see cref="TabPath"/> are denormalised copies of that hierarchy which the legacy write path
/// maintained whenever a page was moved or renamed, and they are carried across as stored columns
/// rather than recomputed on read, so a row round-trips unchanged.
/// </para>
/// <para>
/// Obligations this type places on the Infrastructure layer, which owns mapping under Rules T3 and
/// T4 and may not alter the schema:
/// </para>
/// <list type="bullet">
///   <item>
///   Bind the modernised member names to the unchanged legacy column names. Three differ:
///   <see cref="TabId"/> maps <c>TabID</c>, <see cref="Keywords"/> maps <c>KeyWords</c> and
///   <see cref="ParentId"/> maps <c>ParentId</c> while its reference is named
///   <see cref="Parent"/>. Modernising a property name must never rename a column.
///   </item>
///   <item>
///   Configure the self-referencing hierarchy explicitly, with a delete behaviour that does not
///   conflict with the shipped constraint. <c>FK_Tabs_Tabs</c>
///   (<c>01.00.04.SqlDataProvider</c> line 50) carries no <c>ON DELETE</c> clause - SQL Server
///   forbids a cascade on a self-reference - whereas <c>FK_Tabs_Portals</c>
///   (<c>01.00.05.SqlDataProvider</c> line 1540) does cascade.
///   </item>
///   <item>
///   Map <see cref="IsSecure"/> even though every table definition before
///   <c>04.05.04.SqlDataProvider</c> lines 45-46 lacks it: only the terminal schema is
///   authoritative.
///   </item>
///   <item>
///   Leave <see cref="IconFile"/> unresolved. See that member for the reason and for the note owed
///   to <c>MIGRATION_NOTES.md</c>.
///   </item>
/// </list>
/// <para>
/// There is no unique constraint over the tenant and the page name at the terminal state:
/// <c>IX_Tabs</c> was added at <c>01.00.08.SqlDataProvider</c> line 6072 and dropped again at
/// <c>02.00.01.SqlDataProvider</c> line 64. Duplicate page names are therefore legal at the storage
/// layer, and rejecting them is an Application-layer rule rather than an invariant of this type.
/// </para>
/// </remarks>
public sealed class Tab : Entity<int>
{
    /// <summary>
    /// The value equality is based on, which is always <see cref="TabId"/>.
    /// </summary>
    /// <remarks>
    /// Never mapped to a column; the entity configuration names <see cref="TabId"/> in its
    /// <c>HasKey</c> call instead.
    /// </remarks>
    public override int Identity => TabId;

    /// <summary>
    /// The <c>TabID</c> column: <c>int IDENTITY(0, 1) NOT NULL</c>, the primary key
    /// <c>PK_Tabs</c>, generated by the database.
    /// </summary>
    /// <remarks>
    /// Zero keys a real page, because the identity seed is zero. No comparison against zero - and no
    /// comparison against any other reserved value - may be read as absence, and nothing may infer
    /// from this value whether the row has been written.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The <c>TabOrder</c> column: <c>int NOT NULL</c> defaulting to <c>0</c>, the sort position
    /// among siblings sharing this page's parent.
    /// </summary>
    /// <remarks>
    /// Made <c>NOT NULL</c> with its default reasserted at <c>03.01.01.SqlDataProvider</c> lines
    /// 1278 and 1284. The legacy write path renumbered whole sibling sets, so values are not
    /// guaranteed contiguous and ordering must be by comparison, never by index.
    /// </remarks>
    public int TabOrder { get; set; }

    /// <summary>
    /// The <c>PortalID</c> column: <c>int NULL</c>, the owning tenant, or <see langword="null"/> for
    /// a host page that belongs to the installation rather than to any portal.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is. The legacy property was a non-nullable <c>Integer</c> holding
    /// <c>Null.NullInteger</c>, so host pages were marked by -1 and the legacy <c>IsSuperTab</c>
    /// getter tested for exactly that. Since <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>,
    /// -1 is also a genuine portal key: null here means "no portal", and -1 means "the portal keyed
    /// -1". The two must never be conflated in either direction.
    /// </remarks>
    public int? PortalId { get; set; }

    /// <summary>
    /// The <c>TabName</c> column: <c>nvarchar(50) NOT NULL</c>, the name shown in navigation.
    /// </summary>
    /// <remarks>
    /// Non-nullable because the column is, and carrying no initialiser on purpose. The legacy
    /// representation of "no text" was the empty string rather than <see langword="null"/>, so
    /// seeding this member with <see cref="string.Empty"/> would plant that sentinel in the domain
    /// and make an unsupplied name indistinguishable from a name of zero length.
    /// </remarks>
    public string TabName { get; set; }

    /// <summary>
    /// The <c>IsVisible</c> column: <c>bit NOT NULL</c> defaulting to <c>1</c>, whether the page
    /// appears in navigation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hidden page is still reachable by direct address; this governs navigation rendering only.
    /// </para>
    /// <para>
    /// The initialiser reproduces the store default declared as <c>DF_Tabs_IsVisible DEFAULT (1)</c>
    /// at <c>01.00.00.SqlDataProvider</c> line 497 and reasserted at
    /// <c>03.01.01.SqlDataProvider</c> line 1286, and is deliberate rather than incidental. It is
    /// not one of the banned legacy sentinels: <c>true</c> encodes a real value and never absence,
    /// and this column is not nullable. Expressing the default in the mapping instead would be
    /// unsafe, because a store default is only reached for a column omitted from the insert, and a
    /// <see cref="bool"/> cannot distinguish "not supplied" from "explicitly false" - a request for
    /// a hidden page would silently be stored as visible. Removing it altogether would lose the
    /// store default from every layer.
    /// </para>
    /// </remarks>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// The <c>ParentId</c> column: <c>int NULL</c>, the page directly above this one, or
    /// <see langword="null"/> when this page sits at the root of its portal's hierarchy.
    /// </summary>
    /// <remarks>
    /// The self-reference added with <c>FK_Tabs_Tabs</c> at <c>01.00.04.SqlDataProvider</c> lines
    /// 46-50. Nullable because the column is; as with <see cref="PortalId"/> the legacy sentinel -1
    /// stood for "no parent", and null must never be collapsed back to it.
    /// </remarks>
    public int? ParentId { get; set; }

    /// <summary>
    /// The <c>Level</c> column: <c>int NOT NULL</c> defaulting to <c>0</c>, the depth of this page
    /// in its hierarchy, zero at the root.
    /// </summary>
    /// <remarks>
    /// A denormalised count of the parent chain, maintained by the write path rather than derived on
    /// read, and preserved as stored. The identifier is keyword-adjacent and the schema brackets it
    /// as <c>[Level]</c>; quoting is the mapping layer's concern.
    /// </remarks>
    public int Level { get; set; }

    /// <summary>
    /// The <c>IconFile</c> column: <c>nvarchar(100) NULL</c>, the navigation icon, held exactly as
    /// stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this member carries the RAW base-table value, which for rows written through the
    /// file manager is the token <c>fileid=N</c> rather than a path. The terminal
    /// <c>vw_Tabs</c> view resolves it - "CASE WHEN LEFT(LOWER(T.IconFile), 6) = 'fileid' THEN
    /// (SELECT Folder + FileName FROM Files WHERE 'fileid=' + convert(varchar, Files.FileID) =
    /// T.IconFile) ELSE T.IconFile END" at <c>04.05.04.SqlDataProvider</c> lines 62-71 - by joining
    /// <c>dbo.Files</c>, a table this migration does not model.
    /// </para>
    /// <para>
    /// That projection must not be baked in here: resolving it would require the entity to read
    /// another table, and a resolved value would then be written back over the token on the next
    /// save, destroying the reference. Repository and DTO code that needs a displayable path
    /// resolves the token at the point of projection, and because the difference is externally
    /// observable in any response that exposes this field it is owed a note in
    /// <c>MIGRATION_NOTES.md</c>.
    /// </para>
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// The <c>DisableLink</c> column: <c>bit NOT NULL</c> defaulting to <c>0</c>, whether the
    /// navigation entry is inert - rendered, but not a link.
    /// </summary>
    /// <remarks>
    /// Added at <c>01.00.10.SqlDataProvider</c> line 277 and made <c>NOT NULL</c> with its default
    /// reasserted at <c>03.01.01.SqlDataProvider</c> lines 1281 and 1290. Used for a heading that
    /// groups child pages without being navigable itself.
    /// </remarks>
    public bool DisableLink { get; set; }

    /// <summary>
    /// The <c>Title</c> column: <c>nvarchar(200) NULL</c>, the browser and search-result title,
    /// which the legacy screens allowed to differ from <see cref="TabName"/>.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// The <c>Description</c> column: <c>nvarchar(500) NULL</c>, the page description published as
    /// metadata.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The <c>KeyWords</c> column: <c>nvarchar(500) NULL</c>, comma-separated search keywords
    /// published as metadata.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the member is spelled <c>Keywords</c> while the column it binds to is spelled
    /// <c>KeyWords</c>, with a capital W, as added at <c>02.00.00.SqlDataProvider</c> line 4754. The
    /// single word is the idiomatic C# form and matches the caption the legacy administration screens
    /// showed, and the column name is fixed by the shipped schema. The entity configuration reconciles
    /// the two with an explicit <c>HasColumnName("KeyWords")</c>; modernising the member must not
    /// rename the column, and no other layer should reproduce the bridge.
    /// </remarks>
    public string? Keywords { get; set; }

    /// <summary>
    /// The <c>IsDeleted</c> column: <c>bit NOT NULL</c> defaulting to <c>0</c>, the soft-delete flag
    /// that moves a page into the recycle bin.
    /// </summary>
    /// <remarks>
    /// A deletion sets this flag rather than removing the row, so read paths that present live pages
    /// must filter on it. Added at <c>02.00.00.SqlDataProvider</c> line 4755 and made
    /// <c>NOT NULL</c> with its default reasserted at <c>03.01.01.SqlDataProvider</c> lines 1282 and
    /// 1292.
    /// </remarks>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The <c>Url</c> column: <c>nvarchar(255) NULL</c>, the alternative target of a page that
    /// redirects instead of hosting modules.
    /// </summary>
    /// <remarks>
    /// Added at <c>02.02.00.SqlDataProvider</c> line 40; the terminal view aliases the same column
    /// as <c>URL</c> at <c>04.05.04.SqlDataProvider</c> line 82, which is the identical column under
    /// a case-insensitive identifier. Its content is a discriminated token - an external address,
    /// another page's identifier, or a <c>fileid=N</c> file reference - and the legacy read-only
    /// <c>TabType</c> and <c>FullUrl</c> members classified and expanded it. Both are absent by
    /// design: classification is navigation behaviour, not stored state, and expansion needs a link
    /// generator this layer must not reach.
    /// </remarks>
    public string? Url { get; set; }

    /// <summary>
    /// The <c>SkinSrc</c> column: <c>nvarchar(200) NULL</c>, the skin applied to this page, or
    /// <see langword="null"/> to inherit the portal's.
    /// </summary>
    /// <remarks>
    /// Added at <c>02.02.02.SqlDataProvider</c> line 2541. Preserved so a row round-trips intact
    /// even though the skinning subsystem that consumed it is out of scope; the legacy
    /// <c>SkinPath</c> member that resolved it to a directory is absent.
    /// </remarks>
    public string? SkinSrc { get; set; }

    /// <summary>
    /// The <c>ContainerSrc</c> column: <c>nvarchar(200) NULL</c>, the default container skin for
    /// modules placed on this page, or <see langword="null"/> to inherit the portal's.
    /// </summary>
    /// <remarks>
    /// Added at <c>02.02.02.SqlDataProvider</c> line 2542. Preserved for round-tripping on the same
    /// terms as <see cref="SkinSrc"/>; a placement may override it through its own
    /// <see cref="TabModule.ContainerSrc"/>.
    /// </remarks>
    public string? ContainerSrc { get; set; }

    /// <summary>
    /// The <c>TabPath</c> column: <c>nvarchar(255) NULL</c>, the materialised hierarchy path, for
    /// example <c>//Home//Reports</c>.
    /// </summary>
    /// <remarks>
    /// Added at <c>02.02.02.SqlDataProvider</c> line 3185. A denormalised copy of the parent chain
    /// that the legacy write path rebuilt whenever a page was moved or renamed; preserved as stored
    /// rather than recomputed, so it is authoritative only as far as the last write that maintained
    /// it.
    /// </remarks>
    public string? TabPath { get; set; }

    /// <summary>
    /// The <c>StartDate</c> column: <c>datetime NULL</c>, the instant from which the page is
    /// published, or <see langword="null"/> when it is published immediately.
    /// </summary>
    /// <remarks>
    /// Added at <c>02.02.02.SqlDataProvider</c> line 3186. Nullable because the column is: the
    /// legacy property was a non-nullable <c>Date</c> holding <c>Null.NullDate</c>, so
    /// <c>Date.MinValue</c> stood for "no start". That sentinel is not carried forward and this
    /// member has no initialiser.
    /// </remarks>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The <c>EndDate</c> column: <c>datetime NULL</c>, the instant after which the page stops being
    /// published, or <see langword="null"/> when it never expires.
    /// </summary>
    /// <remarks>
    /// Added at <c>02.02.02.SqlDataProvider</c> line 3187, and nullable for the same reason as
    /// <see cref="StartDate"/>. The two are not constrained relative to each other by the schema, so
    /// ordering them is an Application-layer validation rule.
    /// </remarks>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// The <c>RefreshInterval</c> column: <c>int NULL</c>, the client refresh period in seconds, or
    /// <see langword="null"/> when the page does not refresh itself.
    /// </summary>
    /// <remarks>
    /// Added at <c>03.01.01.SqlDataProvider</c> line 409. Nullable because the column is; the legacy
    /// property held <c>Null.NullInteger</c>, so -1 stood for "no refresh" and must not be
    /// reintroduced as a substitute for null.
    /// </remarks>
    public int? RefreshInterval { get; set; }

    /// <summary>
    /// The <c>PageHeadText</c> column: <c>nvarchar(500) NULL</c>, additional markup injected into
    /// the rendered page head.
    /// </summary>
    /// <remarks>
    /// Added at <c>03.01.01.SqlDataProvider</c> line 410. Stored verbatim, which makes it
    /// caller-supplied markup: anything that renders it is responsible for its own escaping policy.
    /// </remarks>
    public string? PageHeadText { get; set; }

    /// <summary>
    /// The <c>IsSecure</c> column: <c>bit NOT NULL</c> defaulting to <c>0</c>, whether the page must
    /// be served over a secure transport.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this column exists only in the terminal schema, added last in the chain as
    /// <c>IsSecure bit NOT NULL CONSTRAINT DF_Tabs_IsSecure DEFAULT (0)</c> at
    /// <c>04.05.04.SqlDataProvider</c> lines 45-46, and the terminal <c>vw_Tabs</c> selects it at
    /// line 86. It must be mapped even though every earlier table definition lacks it, because the
    /// terminal state is the only authoritative one.
    /// </remarks>
    public bool IsSecure { get; set; }

    /// <summary>
    /// The owning tenant, the inverse of <see cref="Portal.Tabs"/>, or <see langword="null"/> for a
    /// host page and for any page loaded without this reference.
    /// </summary>
    /// <remarks>
    /// The <c>FK_Tabs_Portals</c> end, declared <c>ON DELETE CASCADE</c> at
    /// <c>01.00.05.SqlDataProvider</c> line 1540: deleting a portal removes its pages outright.
    /// Optional in the model for two independent reasons - <see cref="PortalId"/> is nullable, and a
    /// reference that was not loaded is null as well - so its being null never on its own means the
    /// page is a host page. Read <see cref="PortalId"/> for that.
    /// </remarks>
    public Portal? Portal { get; set; }

    /// <summary>
    /// The page directly above this one, the inverse of <see cref="Children"/>, or
    /// <see langword="null"/> at the root of the hierarchy and for any page loaded without this
    /// reference.
    /// </summary>
    /// <remarks>
    /// The <c>FK_Tabs_Tabs</c> end from <c>01.00.04.SqlDataProvider</c> lines 46-50. The constraint
    /// carries no <c>ON DELETE</c> clause, so the mapping must not introduce one: SQL Server rejects
    /// a cascade on a self-reference, and silently reparenting or deleting descendants behind the
    /// service layer is exactly the behaviour the shipped schema declines.
    /// </remarks>
    public Tab? Parent { get; set; }

    /// <summary>
    /// The pages that name this one as their parent, the inverse of <see cref="Parent"/>.
    /// </summary>
    /// <remarks>
    /// Empty rather than null on a newly constructed page, and empty on a page loaded without its
    /// children - the two are indistinguishable here, so emptiness alone does not establish that a
    /// page is a leaf. The legacy <c>HasChildren</c> member is an <c>EXISTS</c> projection in
    /// <c>vw_Tabs</c> rather than a column and is therefore not reproduced: answer the question from
    /// this collection when the tree is loaded, or from a query when it is not.
    /// </remarks>
    public ICollection<Tab> Children { get; } = new List<Tab>();

    /// <summary>
    /// The module placements on this page, each carrying the presentation that varies per placement.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="TabModule.Tab"/>. A module instance reaches a page only through a
    /// placement, which is why no collection of modules appears here; the legacy untyped
    /// <c>Modules</c> ArrayList was page-rendering state, not this relationship.
    /// </remarks>
    public ICollection<TabModule> TabModules { get; } = new List<TabModule>();

    /// <summary>
    /// The permission grants and denials attached to this page.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="TabPermission.Tab"/>, and the sole expression of page authority
    /// since <c>03.00.01.SqlDataProvider</c> lines 1407-1411 dropped the delimited
    /// <c>AuthorizedRoles</c> and <c>AdministratorRoles</c> columns in favour of rows. Evaluating
    /// these grants belongs to the Infrastructure permission evaluator, not to this type.
    /// </remarks>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
