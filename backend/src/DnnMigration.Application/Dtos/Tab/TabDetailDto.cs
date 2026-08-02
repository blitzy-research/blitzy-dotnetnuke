namespace DnnMigration.Application.Dtos.Tab;

/// <summary>
/// The complete read projection for a single page, returned by
/// <c>GET /api/v1/tabs/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Purpose. This type is the detail representation of one page. It is a read projection
/// only: it is never accepted as a request body, and it carries no paging metadata of any
/// kind. Because the endpoint addresses exactly one resource, the value is returned either
/// bare or wrapped in the shared single-resource response envelope — never inside a paged
/// envelope. This type references no envelope type and must not be changed to do so.
/// </para>
/// <para>
/// Legacy source. The shape is derived from three independent, mutually corroborating
/// sources. First, <c>Library/Components/Tabs/TabInfo.vb</c>, whose thirty-six members
/// (thirty-two read/write plus four read-only) were filtered down to those that are
/// genuinely persisted. Second, and most decisively, the row-reading routine
/// <c>FillTabInfo</c> in <c>Library/Components/Tabs/TabController.vb</c>: at lines 83 to
/// 105 it performs exactly twenty-three null-substituting reads, and those twenty-three
/// fields are precisely the twenty-three members declared below. Third, the terminal page
/// read view <c>vw_Tabs</c>, defined in
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.04.SqlDataProvider</c>, which
/// projects the same twenty-three columns.
/// </para>
/// <para>
/// Composition: twenty-two columns plus one projection. Twenty-two of the members map
/// one-to-one onto persisted columns of the legacy <c>Tabs</c> table in its terminal state;
/// the twenty-third, the child-existence flag, is computed rather than stored. Of the
/// twenty-two columns, fourteen are nullable and eight are not — a split that is not a
/// matter of judgement but is fixed by the legacy constructor, which sentinel-initialised
/// exactly the fields its own authors annotated as "the properties that can be null in the
/// database".
/// </para>
/// <para>
/// Derive the column set from the terminal schema, never from the baseline. The legacy
/// <c>Tabs</c> table has a long alteration history, and four of the columns present in the
/// original create-table statement were later dropped outright — two mobile-presentation
/// columns, two pane-width columns — while two role-string columns were dropped in the 3.0
/// upgrade and thereafter recomputed from permissions at read time. None of those six
/// appears here. Anyone revisiting this shape must read the cumulative terminal schema; a
/// reader who consults only the baseline create-table statement will wrongly conclude that
/// members are missing.
/// </para>
/// <para>
/// Relationship to the other two page shapes. Three shapes serve the page endpoints and
/// each is an independent, flat type. The list-row shape carries fourteen members and is
/// the element of the portal-scoped page list. This detail shape carries twenty-three
/// members and is the complete read surface for one page. The update-request shape carries
/// seventeen members, being only the subset a client may actually set. This type is a
/// superset of the list row by content but emphatically not by inheritance: none of the
/// three derives from another, and none references another. Do not "consolidate" them.
/// </para>
/// <para>
/// The portal identifier is deliberately asymmetric across those three shapes, and the
/// asymmetry must not be "harmonised" away. It is present here, because
/// <c>GET /api/v1/tabs/{id}</c> is not portal-scoped by its route and a caller therefore
/// cannot otherwise know which portal the page belongs to. It is absent from the list row,
/// whose route already names the portal, so repeating it on every row would be pure
/// redundancy. It is absent from the update request, because the legacy update procedure
/// accepts no portal argument at all — a page cannot be moved between portals through that
/// path, and the legacy editor never offered the choice, taking the value from ambient page
/// context instead of from a form field.
/// </para>
/// <para>
/// Magic integers: never test an identifier for "absence". Five distinct magic values are
/// live in the legacy page domain, and two of them collide outright with real persisted
/// data:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>-1</c> was the legacy null-integer sentinel, standing for "no parent", "unknown
///     portal" or "host page" — yet <c>-1</c> is simultaneously a legitimate portal
///     identifier, because <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>.
///   </description></item>
///   <item><description>
///     <c>-2</c> meant "detached from the tree" while a page was being deleted or moved.
///   </description></item>
///   <item><description>
///     <c>0</c> is a legitimate page identifier, because <c>Tabs.TabID</c> is declared
///     <c>IDENTITY(0, 1)</c> — and <c>0</c> was also the "recompute this for me" reset
///     value the legacy code passed for the level and order arguments.
///   </description></item>
///   <item><description>
///     <c>int.MinValue</c> meant "any parent" in the legacy lookup-by-name path.
///   </description></item>
///   <item><description>
///     <c>";"</c> meant "no roles" in the legacy permission strings.
///   </description></item>
/// </list>
/// <para>
/// Consequently, never compare an identifier on this type against <c>0</c>, <c>-1</c>,
/// <c>-2</c>, <c>default</c> or <c>int.MinValue</c> in order to decide whether a value is
/// present. Test the nullable members with <c>is null</c> instead. This is also why the
/// domain entity base type exposes no bare identity member and no "is new" or
/// "is transient" helper: <c>default(int)</c> is <c>0</c>, and <c>0</c> is a real stored
/// identity for pages, roles and modules alike.
/// </para>
/// <para>
/// Sentinel boundary. "Sentinels survive at the boundary, not in the domain." The legacy
/// code encoded absence as in-band values — <c>-1</c> for a missing integer,
/// <c>DateTime.MinValue</c> for a missing date and, counter-intuitively, the <em>empty
/// string</em> rather than <c>null</c> for a missing string — and converted every database
/// <c>NULL</c> into one of them on read. The domain model uses honest nullable types
/// instead, and this type is where the resulting external representation is pinned down.
/// Each affected member documents its own legacy sentinel and its target representation
/// below. A mapper populating this type must not quietly rewrite <c>""</c> to <c>null</c>,
/// nor <c>null</c> to <c>""</c>, in either direction: the representation chosen here is
/// documented and stable, because a legacy consumer may be reading the response.
/// </para>
/// <para>
/// Terminology: "Page", not "Tab". The type name retains the legacy <c>Tab</c> vocabulary
/// so that it lines up with the database schema and the <c>Tab</c> domain entity, but the
/// concept an administrator actually sees is a <em>Page</em>. The legacy resource files are
/// explicit about this: the management screen is titled "Page Management", its edit screen
/// "Edit Page", its detail section "Page Details", and the field captions are "Page Name",
/// "Page Title" and "Parent Page". Client-facing labels should therefore say "Page" even
/// though the wire contract says "tab".
/// </para>
/// <para>
/// Inertness. Every member is a trivial auto-property. No member performs input or output,
/// walks the page hierarchy, resolves a file, or computes anything on access. The legacy
/// counter-example is instructive: the legacy page entity's read-only administration-page
/// test (<c>Library/Components/Tabs/TabInfo.vb</c>, lines 435 to 464) read a cache, then a
/// portal controller, then the database, all from inside a property getter. That member is
/// deliberately not carried forward, and no member here may follow its pattern.
/// </para>
/// <para>
/// Serialisation. Property names are emitted in camel case by the API layer's central JSON
/// configuration, yielding <c>tabId</c>, <c>portalId</c>, <c>parentId</c>, <c>keywords</c>,
/// <c>skinSrc</c>, <c>pageHeadText</c> and <c>hasChildren</c>. No serialisation attribute is
/// declared on this type: the legacy XML serialisation attributes and the legacy
/// token-accessor interface are both dropped rather than translated. No validation attribute
/// is declared either — this is a read projection, and no validator exists for it, so every
/// column length quoted below is documentation only.
/// </para>
/// </remarks>
public sealed class TabDetailDto
{
    // MIGRATION: Tabs.TabID is declared IDENTITY(0, 1), so 0 is a real persisted identifier
    // and not an "unset" marker. The domain entity base type therefore deliberately exposes
    // no bare identity member and no is-new/is-transient helper, because default(int) == 0
    // would misreport the very first page in the database as unsaved.
    /// <summary>
    /// Gets or sets the page identifier. Maps the legacy <c>Tabs.TabID</c> column,
    /// <c>int IDENTITY(0, 1) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// Because the column is seeded at zero, <c>0</c> is a legitimate persisted identifier
    /// belonging to a real page — the first page created in a DotNetNuke database has
    /// identifier <c>0</c>. Never treat <c>0</c>, or the equivalent <c>default(int)</c>, as
    /// meaning absent, unset or not yet saved. The legacy recycle-bin list control bound
    /// this field as its item value under the spelling <c>TabId</c>, which is the casing
    /// adopted here.
    /// </remarks>
    public int TabId { get; set; }

    // MIGRATION: TabOrder is server-owned and read-only to clients. The legacy update path
    // accepted no order argument at all: the update procedure's parameter list and its
    // UPDATE body both omit the column, and the page-ordering routine was invoked with 0 for
    // both level and order precisely so that it would recalculate them. Surfacing the value
    // here is a read convenience only; the corresponding update request shape omits it.
    /// <summary>
    /// Gets or sets the sort position of this page among its siblings under the same parent.
    /// Maps the legacy <c>Tabs.TabOrder</c> column, <c>int NOT NULL</c> defaulting to
    /// <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This value is computed by the server's page-ordering routine and is read-only from a
    /// client's perspective — the legacy page editor exposed no control for it anywhere on
    /// its form. It is surfaced so that a caller can render pages in their true sibling
    /// order without a second request. The legacy read path could yield the <c>-1</c>
    /// integer sentinel through its null-substituting reader, but the terminal schema
    /// declares the column <c>NOT NULL</c> with a default of <c>0</c>, so the target type is
    /// a non-nullable integer rather than a nullable one.
    /// </remarks>
    public int TabOrder { get; set; }

    // MIGRATION: the legacy page entity declared PortalID as a non-nullable VB Integer that
    // the constructor sentinel-initialised to -1, even though the column is
    // `PortalID int NULL`. The -1 did not merely mean "unknown": it meant the page is a
    // host-level page rather than a portal page. Two independent legacy sites prove it — the
    // super-page test returned `(PortalID = Null.NullInteger)`, and the controller branched
    // on `If Not Null.IsNull(objTab.PortalID) Then ... Else ' host tab`. The target models
    // this as a nullable integer whose null means "host-level page", so a host page
    // serialises portalId as JSON null and NOT as -1. The sentinel must not survive into the
    // wire contract, because -1 is simultaneously a legitimate Portals.PortalID value under
    // IDENTITY(-1, 1).
    /// <summary>
    /// Gets or sets the identifier of the portal that owns this page, or
    /// <see langword="null"/> when the page is a host-level page rather than a portal page.
    /// Maps the legacy <c>Tabs.PortalID</c> column, <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy type was a non-nullable integer using <c>-1</c> to mean "host page". The
    /// target representation is a nullable integer in which <see langword="null"/> carries
    /// that same meaning, so a host-level page serialises as JSON <c>null</c> and never as
    /// <c>-1</c>. Test for a host-level page with <c>PortalId is null</c>.
    /// </para>
    /// <para>
    /// The collision that forces this change: <c>-1</c> is also a perfectly valid portal
    /// identifier, because <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c> — so
    /// the first real portal has identifier <c>0</c> and <c>-1</c> is at once a real portal
    /// identifier and the legacy "absent" marker. Preserving the sentinel would make those
    /// two cases indistinguishable on the wire.
    /// </para>
    /// <para>
    /// This member is present here but absent from both sibling page shapes, and that
    /// three-way asymmetry is deliberate; see the type-level documentation. Note also that
    /// the legacy editor never let an administrator choose the value: it was assigned from
    /// ambient page context, not from a form field.
    /// </para>
    /// </remarks>
    public int? PortalId { get; set; }

    /// <summary>
    /// Gets or sets the page name, captioned "Page Name" in the legacy administration
    /// screens. Maps the legacy <c>Tabs.TabName</c> column, <c>nvarchar(50) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text describes this as the name of the page, whose text "will be
    /// displayed in the menu system"; it was the only field on the legacy editor carrying a
    /// required-field validator, and the legacy lists bound it as their display text. The
    /// maximum length of 50 characters — confirmed by the column declaration, the terminal
    /// update procedure's parameter and the editor's own input limit alike — is recorded for
    /// documentation only, because this type is a read projection and declares no validation
    /// attribute.
    /// </remarks>
    // MIGRATION: initialised rather than left to the global CS8618 suppression, which exists for
    // ORM-materialised entities backed by a NOT NULL column that rejects a null loudly. This type is
    // built by a hand-written mapper with no such backstop, so an unset member would present a null
    // through a non-nullable contract. The update request that pairs with this projection initialises
    // its own copy of this member for the same reason.
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this page appears in the navigation menu.
    /// Captioned "Include In Menu" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.IsVisible</c> column, <c>bit NOT NULL</c> defaulting to <c>1</c>.
    /// </summary>
    /// <remarks>
    /// The member name is inherited from the schema, but this is not a general-purpose
    /// visibility flag and must not be read as one: it controls menu inclusion only. The
    /// legacy caption was "Include In Menu" and the legacy help text read "You have the
    /// choice on whether or not to include the page in the main navigation menu. If a page
    /// is not included in the menu, you can still link to it based on its page URL." A page
    /// with this flag clear therefore remains reachable by direct link, and is neither
    /// deleted nor disabled.
    /// </remarks>
    public bool IsVisible { get; set; }

    // MIGRATION: the legacy page entity declared ParentId as a non-nullable VB Integer whose
    // "no parent" value was the in-band sentinel -1, even though the underlying column is
    // `ParentId int NULL`. The target models it as a nullable integer with null meaning
    // "root-level page", so a root page serialises parentId as JSON null and NOT as -1. That
    // is a deliberate, documented change of external representation rather than an accident
    // of serialisation. Absence must be tested with `is null`, never with `== -1` or `<= 0`,
    // because -1 and 0 are both legitimate identifiers elsewhere in this schema.
    /// <summary>
    /// Gets or sets the identifier of this page's parent page, or <see langword="null"/> when
    /// the page sits at the root of the hierarchy. Maps the legacy <c>Tabs.ParentId</c>
    /// column, <c>int NULL</c>. Captioned "Parent Page" in the legacy administration screens,
    /// where the root choice was labelled "Root".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy type was a non-nullable integer that used <c>-1</c> to mean "no parent",
    /// and the legacy parent picker's "Root" option carried the literal value <c>-1</c>. The
    /// target representation is a nullable integer in which <see langword="null"/> means
    /// "root-level page", so a root-level page serialises as JSON <c>null</c> and never as
    /// <c>-1</c>.
    /// </para>
    /// <para>
    /// Test for a root-level page with <c>ParentId is null</c>. Never write
    /// <c>ParentId == -1</c> or <c>ParentId &lt;= 0</c> as an absence check: <c>0</c> is a
    /// legitimate page identifier under <c>IDENTITY(0, 1)</c>, <c>-1</c> is a legitimate
    /// portal identifier under <c>IDENTITY(-1, 1)</c>, and the legacy code additionally used
    /// <c>-2</c> for a page detached from the tree and <c>int.MinValue</c> for "any parent".
    /// </para>
    /// <para>
    /// The legacy help text for the corresponding editor field reads: "Select the page that
    /// you would like this page to be a child of."
    /// </para>
    /// <para>
    /// Known legacy behaviour, preserved and not corrected. When an administrator chose a new
    /// parent that would have created a circular reference, the legacy editor performed the
    /// update only if the guard passed and otherwise fell through silently — no message was
    /// shown and no error was raised, so the administrator's edit was discarded without
    /// feedback. That behaviour is recorded here rather than repaired, because behavioural
    /// equivalence takes precedence over opportunistic correction.
    /// </para>
    /// </remarks>
    public int? ParentId { get; set; }

    // MIGRATION: Level is server-derived, not client-supplied. The legacy update path
    // accepted no level argument: the update procedure omits the column entirely, and the
    // page-ordering routine was called with 0 for both level and order so that it would
    // recompute the whole subtree itself. It is surfaced here purely so a client can render
    // an indented tree without a second request; the update request shape omits it.
    /// <summary>
    /// Gets or sets the depth of this page in the hierarchy, where <c>0</c> denotes a
    /// root-level page. Maps the legacy <c>Tabs.Level</c> column, <c>int NOT NULL</c>
    /// defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This value is derived by the server from the parent chain and is read-only from a
    /// client's perspective — the legacy page editor exposed no control for it. It exists on
    /// this projection so that a tree or indented list can be rendered from a single
    /// response, without the caller having to walk the hierarchy or issue further requests.
    /// As with the sort position, the legacy reader could yield the <c>-1</c> integer
    /// sentinel, but the terminal schema declares the column <c>NOT NULL</c> with a default
    /// of <c>0</c>, so the target type is a non-nullable integer.
    /// </remarks>
    public int Level { get; set; }

    // MIGRATION: IconFile carries whatever the mapper supplies, which may be a raw
    // `fileid=NNN` token rather than a usable path. The base table stores the raw value; only
    // the legacy read view resolved it, with
    //   CASE WHEN LEFT(LOWER(T.IconFile), 6) = 'fileid'
    //        THEN (SELECT Folder + FileName FROM Files
    //              WHERE 'fileid=' + convert(varchar, Files.FileID) = T.IconFile)
    //        ELSE T.IconFile END
    // and the legacy editor assigned the raw control value straight onto the entity. The
    // domain entity intentionally stores the raw base-table value and defers resolution to
    // the repository/mapper layer. Resolution must NOT be attempted here: it requires a file
    // lookup, and no member of a DTO may perform input or output.
    /// <summary>
    /// Gets or sets the reference to the menu icon for this page. Captioned "Icon" in the
    /// legacy administration screens. Maps the legacy <c>Tabs.IconFile</c> column,
    /// <c>nvarchar(100) NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "You can choose an icon that can be used in the menu."
    /// </para>
    /// <para>
    /// This member carries whatever value the mapper supplies. The base table stores a raw
    /// value that may be either a path or a <c>fileid=NNN</c> token, and only the legacy read
    /// view resolved the token form into a folder-and-file-name path; the legacy editor wrote
    /// the unresolved token. Resolving it is the responsibility of the page mapper or the
    /// repository, not of this type, because resolution requires a file lookup and no member
    /// of a DTO may perform input or output. Consumers must therefore tolerate an unresolved
    /// token unless the supplying mapper documents that it resolves them.
    /// </para>
    /// <para>
    /// The legacy value for "no icon" was the empty string rather than <c>null</c>, since the
    /// legacy null-string sentinel was <c>""</c>; the target representation is a nullable
    /// string and a mapper must not silently convert between the two. The maximum length of
    /// 100 characters is documentation only.
    /// </para>
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page is disabled. Captioned "Disabled" in
    /// the legacy administration screens. Maps the legacy <c>Tabs.DisableLink</c> column,
    /// <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "If the page is disabled it is not available to users of
    /// the site.  You can use this option to suppress content that you might wish to show at
    /// a later time." A disabled page typically still renders in the menu as inert text
    /// rather than as a link.
    /// </para>
    /// <para>
    /// Known legacy behaviour, preserved and not corrected. The legacy page editor assigned
    /// this flag only when the page being edited was not one of five protected system pages —
    /// the admin, splash, home, login and user pages. For those five the posted value was
    /// silently discarded and the flag retained its default of <see langword="false"/>, with
    /// no message shown to the administrator. That behaviour is recorded here rather than
    /// repaired, because behavioural equivalence takes precedence over opportunistic
    /// correction; any change would be a deliberate, separately documented decision.
    /// </para>
    /// </remarks>
    public bool DisableLink { get; set; }

    /// <summary>
    /// Gets or sets the page title, captioned "Page Title" in the legacy administration
    /// screens. Maps the legacy <c>Tabs.Title</c> column, <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text explains that this text "will be displayed in the browser window
    /// title". The legacy representation of "no title" was the empty string rather than
    /// <c>null</c>, because the legacy null-string sentinel was <c>""</c>; the target
    /// representation is a nullable string. A mapper must not silently convert between the
    /// two — whichever value the repository supplies is the value serialised. The maximum
    /// length of 200 characters is documentation only. Note that the legacy editor's input
    /// control permitted more characters than the column accepts, so a legacy row can never
    /// exceed 200 but a legacy user could attempt to.
    /// </remarks>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the free-text description of this page, captioned "Description" in the
    /// legacy administration screens. Maps the legacy <c>Tabs.Description</c> column,
    /// <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "Enter a description about this page here." The legacy
    /// representation of "no description" was the empty string rather than <c>null</c>,
    /// because the legacy null-string sentinel was <c>""</c>; the target representation is a
    /// nullable string, and a mapper must not silently convert between the two in either
    /// direction. The maximum length of 500 characters is documentation only.
    /// </remarks>
    public string? Description { get; set; }

    // MIGRATION: property name spelling. The database column is spelled `KeyWords`, with a
    // capital W in the middle, and so was the legacy property. This member is spelled
    // `Keywords`, the idiomatic single-word form, which also matches the legacy user-facing
    // caption "Keywords". The persistence layer is responsible for bridging the two by
    // mapping Keywords onto the KeyWords column; nothing else in the target should reproduce
    // the legacy spelling.
    /// <summary>
    /// Gets or sets the comma-separated search keywords for this page, captioned "Keywords"
    /// in the legacy administration screens. Maps the legacy <c>Tabs.KeyWords</c> column,
    /// <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelling. The column and the legacy property are spelled <c>KeyWords</c>; this member
    /// is spelled <c>Keywords</c>, matching both idiomatic naming and the legacy caption
    /// "Keywords". The entity configuration in the persistence layer maps the one onto the
    /// other, so the difference is invisible to callers and must not be propagated into the
    /// wire contract, where the serialised name is <c>keywords</c>.
    /// </para>
    /// <para>
    /// The legacy help text reads "Enter some keywords for this page (separated by commas).
    /// These keywords are used by search engines to help index your site's pages." The
    /// legacy representation of "no keywords" was the empty string rather than <c>null</c>,
    /// since the legacy null-string sentinel was <c>""</c>; the target representation is a
    /// nullable string, and a mapper must not silently convert between the two. The maximum
    /// length of 500 characters is documentation only.
    /// </para>
    /// </remarks>
    public string? Keywords { get; set; }

    // MIGRATION: IsDeleted is exposed on this projection, never filtered or hidden at the DTO
    // level — the service or endpoint decides which rows to return. A soft-deleted page is
    // still a row. In the legacy code deletion and restoration were both plain flag writes
    // through the very same update call: the controller set IsDeleted = True and called
    // UpdateTab, auditing the change as "sent to recycle bin", while the recycle-bin screen
    // set IsDeleted = False and called the identical UpdateTab, auditing it as "restored".
    // Because both directions flow through that one path, the flag is also present on the
    // update request shape, and the read/write pair is coherent by construction.
    /// <summary>
    /// Gets or sets a value indicating whether this page has been soft-deleted into the
    /// recycle bin. Maps the legacy <c>Tabs.IsDeleted</c> column, <c>bit NOT NULL</c>
    /// defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A soft-deleted page remains a row in the table: deletion is a flag, not a removal, and
    /// the legacy recycle-bin screen enumerated pages by testing exactly this flag. The value
    /// is therefore surfaced rather than suppressed, and this projection neither filters nor
    /// hides it. Deciding which pages to return is the responsibility of the service or the
    /// endpoint, not of this type.
    /// </para>
    /// <para>
    /// Restoration was symmetrical in the legacy code — the same update call with the flag
    /// cleared — but it was guarded: a page whose parent was itself still deleted could not
    /// be restored, and the legacy screen reported "Page Cannot Be Restored Until Its Parent
    /// Is Restored First." That ordering rule is application logic and belongs to the page
    /// service, not to this projection.
    /// </para>
    /// </remarks>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the navigation target used when this page acts as a link to another
    /// resource rather than hosting content of its own. Captioned "Link Url" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.Url</c> column,
    /// <c>nvarchar(255) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "If you would like this page to behave as a navigation link
    /// to another resource, you can specify the Link URL value here. Please note that this
    /// field is optional." Note the casing quirk in the legacy schema: the base table spells
    /// the column <c>Url</c> while the terminal read view projects it under the upper-case
    /// alias <c>URL</c>; the target contract uses the single spelling declared here. The
    /// legacy value for "not a link" was the empty string rather than <c>null</c>, since the
    /// legacy null-string sentinel was <c>""</c>; the target representation is a nullable
    /// string and a mapper must not silently convert between the two. The maximum length of
    /// 255 characters is documentation only.
    /// </remarks>
    public string? Url { get; set; }

    // MIGRATION: SkinSrc is a real, persisted column and is written by the terminal update
    // procedure, so it belongs on both the read and the write surface. Skinning itself,
    // however, is excluded from this migration: the API stores and returns this value as an
    // opaque token and performs no skin resolution, no file lookup and no rendering anywhere.
    // That is why the legacy entity's derived, presentation-only resolved-path counterpart is
    // deliberately absent from this projection while this column is present.
    /// <summary>
    /// Gets or sets the opaque skin source token applied to this page, captioned "Page Skin"
    /// in the legacy administration screens. Maps the legacy <c>Tabs.SkinSrc</c> column,
    /// <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "The selected skin will be applied to this page." The value
    /// is treated as an opaque token: it is stored and returned verbatim, and no component in
    /// the target resolves, loads or renders a skin. The legacy entity also carried a derived
    /// resolved-path companion member for presentation, which is excluded here precisely
    /// because skinning is excluded and because resolving a path would require input or
    /// output from a property getter.
    /// </para>
    /// <para>
    /// The legacy value for "no skin" was the empty string rather than <c>null</c>, since the
    /// legacy null-string sentinel was <c>""</c>; the target representation is a nullable
    /// string and a mapper must not silently convert between the two. The maximum length of
    /// 200 characters is documentation only.
    /// </para>
    /// </remarks>
    public string? SkinSrc { get; set; }

    // MIGRATION: ContainerSrc is a real, persisted column and is written by the terminal
    // update procedure, so it belongs on both the read and the write surface. As with the
    // skin token, container handling itself is excluded from this migration: the value is
    // stored and returned as an opaque token, with no resolution, file lookup or rendering
    // anywhere in the target, and the legacy derived resolved-path counterpart is therefore
    // deliberately absent from this projection.
    /// <summary>
    /// Gets or sets the opaque container source token applied to the modules on this page,
    /// captioned "Page Container" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.ContainerSrc</c> column, <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "The selected container will be applied to all modules on
    /// this page." The value is treated as an opaque token: it is stored and returned
    /// verbatim, and no component in the target resolves, loads or renders a container. The
    /// legacy entity's derived resolved-path companion member is excluded here for the same
    /// two reasons that apply to the skin token — container rendering is excluded from this
    /// migration, and path resolution would require input or output from a property getter.
    /// </para>
    /// <para>
    /// The legacy value for "no container" was the empty string rather than <c>null</c>,
    /// since the legacy null-string sentinel was <c>""</c>; the target representation is a
    /// nullable string and a mapper must not silently convert between the two. The maximum
    /// length of 200 characters is documentation only.
    /// </para>
    /// </remarks>
    public string? ContainerSrc { get; set; }

    // MIGRATION: TabPath is server-derived. The legacy code assigned it exclusively by calling
    // GenerateTabPath(ParentId, TabName) — at four distinct sites, three in the page
    // controller and one in the page editor code-behind — so it was never authored by hand and
    // never accepted from the client, even though the update procedure does persist it. The
    // legacy code also cascaded it: whenever a page's name or parent changed, the controller
    // recursively regenerated the path of every descendant. That cascade is page-service
    // business logic and emphatically not DTO logic. The update request shape omits this
    // member for the same reason.
    /// <summary>
    /// Gets or sets the materialised hierarchical path of this page. Maps the legacy
    /// <c>Tabs.TabPath</c> column, <c>nvarchar(255) NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is generated by the server from the parent chain and the page name, and is
    /// read-only from a client's perspective — the legacy page editor exposed no control for
    /// it. It is a denormalisation of the hierarchy, surfaced here for display and matching so
    /// that a caller need not reconstruct it.
    /// </para>
    /// <para>
    /// Renaming a page or moving it to a new parent invalidates the stored path of every page
    /// beneath it, and the legacy code responded by regenerating the whole subtree. Any code
    /// that writes a page name or parent must therefore trigger the equivalent cascade in the
    /// page service; nothing about that behaviour belongs to this projection.
    /// </para>
    /// <para>
    /// The legacy representation of "no path" was the empty string rather than <c>null</c>,
    /// because the legacy null-string sentinel was <c>""</c>; the target representation is a
    /// nullable string, and a mapper must not silently convert between the two in either
    /// direction. The maximum length of 255 characters is documentation only.
    /// </para>
    /// </remarks>
    public string? TabPath { get; set; }

    // MIGRATION: the legacy page entity declared StartDate as a non-nullable VB Date that the
    // constructor sentinel-initialised to DateTime.MinValue, and the legacy editor wrote that
    // sentinel back whenever the start-date box was left empty, even though the column is
    // `StartDate datetime NULL`. The target models it as a nullable date-time, so an unset
    // start date serialises as JSON null and NOT as 0001-01-01. Do not treat
    // DateTime.MinValue, or default(DateTime), as meaning "unset" on this contract.
    /// <summary>
    /// Gets or sets the date from which this page becomes available, or
    /// <see langword="null"/> when no start date is set. Captioned "Start Date:" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.StartDate</c> column,
    /// <c>datetime NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "Enter the start date for displaying this page. You may use
    /// the Calendar to pick a date." The legacy representation of "no start date" was
    /// <c>DateTime.MinValue</c> — the legacy null-date sentinel — which the editor assigned
    /// explicitly whenever the input was blank. The target representation is
    /// <see langword="null"/>, so an unset start date serialises as JSON <c>null</c> and never
    /// as <c>0001-01-01</c>. Test with <c>StartDate is null</c>.
    /// </para>
    /// <para>
    /// No relationship between the two dates is validated, and none should be documented as
    /// though it were. The legacy editor attached only a data-type check to each date field —
    /// a format check on a single value, reporting "Invalid Start Date" — so there was never
    /// any rule requiring the end date to follow the start date. That absence is faithfully
    /// preserved: introducing such a rule would be a behavioural change, not a bug fix.
    /// </para>
    /// </remarks>
    public DateTime? StartDate { get; set; }

    // MIGRATION: the legacy page entity declared EndDate as a non-nullable VB Date that the
    // constructor sentinel-initialised to DateTime.MinValue, and the legacy editor wrote that
    // sentinel back whenever the end-date box was left empty, even though the column is
    // `EndDate datetime NULL`. The target models it as a nullable date-time, so an unset end
    // date serialises as JSON null and NOT as 0001-01-01. As with the start date, no
    // cross-field ordering rule existed in the legacy application and none is introduced here.
    /// <summary>
    /// Gets or sets the date after which this page ceases to be available, or
    /// <see langword="null"/> when no end date is set. Captioned "End Date:" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.EndDate</c> column,
    /// <c>datetime NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "Enter the end date for displaying this page. You may use
    /// the Calendar to pick a date." The legacy representation of "no end date" was
    /// <c>DateTime.MinValue</c> — the legacy null-date sentinel — which the editor assigned
    /// explicitly whenever the input was blank. The target representation is
    /// <see langword="null"/>, so an unset end date serialises as JSON <c>null</c> and never
    /// as <c>0001-01-01</c>. Test with <c>EndDate is null</c>.
    /// </para>
    /// <para>
    /// The legacy application did not verify that this date follows the start date. Its only
    /// check on the field was a data-type check reporting "Invalid End Date", which validates
    /// the format of one value in isolation. Callers must not assume any ordering between the
    /// two dates, and no such constraint is asserted anywhere in this contract.
    /// </para>
    /// </remarks>
    public DateTime? EndDate { get; set; }

    // MIGRATION: the legacy page entity declared RefreshInterval as a non-nullable VB Integer
    // that the constructor sentinel-initialised to -1, even though the column is
    // `RefreshInterval int NULL`. The legacy editor assigned a value only when its input was
    // both non-empty and numeric, leaving the -1 in place otherwise, so -1 genuinely reached
    // storage as "no automatic refresh". The target models it as a nullable integer, so the
    // absence of a refresh serialises as JSON null and NOT as -1. The legacy field carried no
    // validator of any kind; none is invented here.
    /// <summary>
    /// Gets or sets the automatic page refresh interval <b>in seconds</b>, or
    /// <see langword="null"/> when the page does not refresh automatically. Captioned "Refresh
    /// Interval (seconds)" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.RefreshInterval</c> column, <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit is seconds, on the authority of the legacy caption "Refresh Interval
    /// (seconds)". It is stated explicitly because the column name alone does not carry the
    /// unit and a consumer could otherwise reasonably guess milliseconds or minutes.
    /// </para>
    /// <para>
    /// The legacy representation of "no automatic refresh" was <c>-1</c>, the legacy
    /// null-integer sentinel, which survived into storage because the editor only overwrote it
    /// when the supplied text was non-empty and numeric. The target representation is
    /// <see langword="null"/>, so no automatic refresh serialises as JSON <c>null</c> and
    /// never as <c>-1</c>. Test with <c>RefreshInterval is null</c>.
    /// </para>
    /// <para>
    /// The legacy editor attached no validator to this field at all — not a required-field
    /// check, not a range check, not a numeric check beyond the silent test described above.
    /// No validation rule is asserted here either, in keeping with the read-projection nature
    /// of this type.
    /// </para>
    /// </remarks>
    public int? RefreshInterval { get; set; }

    /// <summary>
    /// Gets or sets the raw markup injected into the document head when this page is rendered,
    /// captioned "Page Header Tags" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.PageHeadText</c> column, <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy editor presented this as a free-form multi-line field and stored whatever
    /// was typed, so the value is arbitrary author-supplied markup rather than a structured
    /// value. It is carried verbatim: this projection neither parses, validates, sanitises nor
    /// escapes it, and any consumer that renders it into a document head is responsible for
    /// its own escaping decisions. The legacy value for "no header tags" was the empty string
    /// rather than <c>null</c>, since the legacy null-string sentinel was <c>""</c>; the
    /// target representation is a nullable string and a mapper must not silently convert
    /// between the two. The maximum length of 500 characters is documentation only.
    /// </remarks>
    public string? PageHeadText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page must be served over a secure
    /// connection. Captioned "Secure?" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.IsSecure</c> column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This is the newest column on the page table, and it exists only in the terminal schema:
    /// it was introduced by the last upgrade script in the legacy chain, which added it as
    /// <c>bit NOT NULL</c> with a default of <c>0</c> and, in the same script, rebuilt the
    /// page read view to project it. Installations upgraded from any earlier version therefore
    /// acquired the column with a blanket default rather than with a per-page decision, so a
    /// value of <see langword="false"/> should be read as "not yet chosen" at least as often
    /// as "deliberately insecure". The legacy help text notes that the setting took effect
    /// only where the administrator had already enabled secure connections for the site as a
    /// whole.
    /// </remarks>
    public bool IsSecure { get; set; }

    // MIGRATION: HasChildren is a computed projection, not a persisted column — it is absent
    // from the Tabs table and from the Tab domain entity, and is populated by the repository
    // or the application service. The legacy terminal read view produced it with
    //   CASE WHEN EXISTS (SELECT 1 FROM Tabs T2 WHERE T2.ParentId = T.TabId)
    //        THEN 'true' ELSE 'false' END AS 'HasChildren'
    // which returns the STRINGS 'true'/'false' rather than a bit — which is exactly why the
    // legacy reader had to wrap this one field in Convert.ToBoolean. This DTO changes the
    // external representation to a genuine JSON boolean. It is also the reason the member sits
    // last here: the twenty-two members above are persisted columns, and this one is not.
    /// <summary>
    /// Gets or sets a value indicating whether any other page names this page as its parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a persisted column and not a member of the page domain entity. It is a
    /// computed existence projection, populated by the repository or the application service,
    /// and it exists so that a client can decide whether to render an expander on a hierarchy
    /// node without issuing a second request. Nothing writes it back: it cannot be set
    /// meaningfully by a caller, and the update request shape has no counterpart for it.
    /// </para>
    /// <para>
    /// The legacy read view computed the same fact but emitted it as the string <c>'true'</c>
    /// or <c>'false'</c>, which the legacy reader then coerced with a boolean conversion. The
    /// target contract is a real boolean and serialises as JSON <c>true</c> or <c>false</c>,
    /// never as a quoted string. Consumers must not parse it as text.
    /// </para>
    /// <para>
    /// The flag has operational significance beyond rendering: the legacy delete path refused
    /// to remove a page while it still had children, and the legacy restore path refused to
    /// restore a page whose parent remained deleted. Enforcing those rules is the
    /// responsibility of the page service, but this flag is the datum a client needs in order
    /// to anticipate them.
    /// </para>
    /// </remarks>
    public bool HasChildren { get; set; }
}
