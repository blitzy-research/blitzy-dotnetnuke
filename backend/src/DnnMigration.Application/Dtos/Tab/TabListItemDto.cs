namespace DnnMigration.Application.Dtos.Tab;

/// <summary>
/// A single row in the page list returned by <c>GET /api/v1/portals/{id}/tabs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Purpose. This type is the element carried inside the paged envelope for the portal
/// page-list endpoint. It is a read projection only: it is never accepted as a request
/// body, and it deliberately carries no paging metadata of its own. Item count, page
/// index and page size belong to the surrounding envelope — the paged-result type in the
/// service layer and the paged-response type on the wire — never to an individual row.
/// This type references neither envelope and must not be changed to do so.
/// </para>
/// <para>
/// Legacy source. The shape is derived from <c>Library/Components/Tabs/TabInfo.vb</c>
/// together with the terminal <c>vw_Tabs</c> read view defined in
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.04.SqlDataProvider</c>. Every
/// member declared below is projected by that view. The legacy list control itself
/// (<c>Website/admin/Tabs/tabs.ascx</c>) bound only two fields — <c>TabName</c> for
/// display and <c>TabId</c> for the value — so the remaining twelve members exist so that
/// a client can render the indented page hierarchy and reproduce the legacy screen's own
/// filtering without a second round trip.
/// </para>
/// <para>
/// Terminology: "Page", not "Tab". The type name retains the legacy <c>Tab</c> vocabulary
/// so that it lines up with the database schema and the <c>Tab</c> domain entity, but the
/// concept an administrator actually sees is a <em>Page</em>. The legacy resource files
/// are explicit about this: the management screen is titled "Page Management", its list is
/// "Pages", its create action is "Add a New Page", and the field captions are "Page Name",
/// "Page Title" and "Parent Page". Client-facing labels should therefore say "Page" even
/// though the wire contract says "tab".
/// </para>
/// <para>
/// Deliberate omission of the portal identifier. This DTO has no <c>PortalId</c> member,
/// and that is by design rather than by oversight. The route is
/// <c>GET /api/v1/portals/{id}/tabs</c>, so every row in the response already belongs to
/// the portal named in the path by construction; repeating that identifier on each row
/// would be pure redundancy. Do not add it. The asymmetry with the single-page detail
/// shape — which does carry a portal identifier, because <c>GET /api/v1/tabs/{id}</c> is
/// not portal-scoped by its route — is intentional.
/// </para>
/// <para>
/// Magic integers: never test an identifier for "absence". Five distinct magic values are
/// live in the legacy page domain, and several of them collide with real persisted data:
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
/// domain entity base type exposes no bare <c>Id</c> member and no "is new" or
/// "is transient" helper: <c>default(int)</c> is <c>0</c>, and <c>0</c> is a real stored
/// identity for pages, roles and modules alike.
/// </para>
/// <para>
/// Sentinel boundary. "Sentinels survive at the boundary, not in the domain." The legacy
/// code encoded absence as in-band values — <c>-1</c> for a missing integer and,
/// counter-intuitively, the <em>empty string</em> rather than <c>null</c> for a missing
/// string — and converted every database <c>NULL</c> into one of them on read. The domain
/// model uses honest nullable types instead, and this DTO is where the resulting external
/// representation is pinned down. Each affected member documents its own legacy sentinel
/// and its target representation below. A mapper populating this type must not quietly
/// rewrite <c>""</c> to <c>null</c>, nor <c>null</c> to <c>""</c>, in either direction:
/// the representation chosen here is documented and stable, because a legacy consumer may
/// be reading the response.
/// </para>
/// <para>
/// Inertness. Every member is a trivial auto-property. No member performs input or
/// output, walks the page hierarchy, or computes anything on access. The legacy
/// counter-example is instructive: the legacy page entity's read-only administration-page
/// test (<c>Library/Components/Tabs/TabInfo.vb</c>, lines 435 to 464) read a cache, then a
/// portal controller, then the database, all from inside a property getter. That member is
/// deliberately not carried forward, and no member here may follow its pattern.
/// </para>
/// <para>
/// Serialisation. Property names are emitted in camel case by the API layer's central
/// JSON configuration, yielding <c>tabId</c>, <c>tabName</c>, <c>parentId</c> and
/// <c>hasChildren</c>. No serialisation attribute is declared on this type: the legacy
/// XML serialisation attributes and the legacy token-accessor interface are both dropped
/// rather than translated. No validation attribute is declared either — this is a read
/// projection, so the column lengths quoted below are documentation only.
/// </para>
/// </remarks>
public sealed class TabListItemDto
{
    // MIGRATION: Tabs.TabID is declared IDENTITY(0, 1), so 0 is a real persisted
    // identifier and not an "unset" marker. The domain entity base type therefore
    // deliberately exposes no bare Id member and no IsNew/IsTransient helper, since
    // default(int) == 0 would misreport the very first page in the database as transient.
    /// <summary>
    /// Gets or sets the page identifier. Maps the legacy <c>Tabs.TabID</c> column.
    /// </summary>
    /// <remarks>
    /// <c>Tabs.TabID</c> is declared <c>IDENTITY(0, 1)</c>, so <c>0</c> is a legitimate
    /// persisted identifier belonging to a real page — the first page created in a
    /// DotNetNuke database has identifier <c>0</c>. Never treat <c>0</c>, or the
    /// equivalent <c>default(int)</c>, as meaning absent, unset or not yet saved.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// Gets or sets the page name, captioned "Page Name" in the legacy administration
    /// screens. Maps the legacy <c>Tabs.TabName</c> column, <c>nvarchar(50) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// This is the display text the legacy page list bound to, by way of
    /// <c>datatextfield="TabName"</c> on its list control, and the legacy help text
    /// describes it as the name of the page whose text "will be displayed in the menu
    /// system". The maximum length of 50 characters is recorded for documentation only:
    /// this type is a read projection, so it declares no validation attribute and no
    /// validator exists for it.
    /// </remarks>
    // MIGRATION: initialised on the same terms as the detail projection's counterpart. This is the
    // member the legacy page list bound as its display text, so a null here would render as a blank,
    // unselectable row rather than failing visibly.
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the page title, captioned "Page Title" in the legacy administration
    /// screens. Maps the legacy <c>Tabs.Title</c> column, <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text explains that this text "will be displayed in the browser
    /// window title". The legacy representation of "no title" was the empty string rather
    /// than <c>null</c>, because the legacy null-string sentinel was <c>""</c>; the target
    /// representation is a nullable string. A mapper must not silently convert between the
    /// two — whichever value the repository supplies is the value serialised. The maximum
    /// length of 200 characters is documentation only.
    /// </remarks>
    public string? Title { get; set; }

    // MIGRATION: TabOrder is server-owned. The legacy update path did not accept an order
    // argument at all: UpdateTab passed no TabOrder parameter, and the page-ordering
    // routine was invoked with 0 for both level and order precisely so that it would
    // recalculate them. Surfacing the value here is a read convenience only; the
    // corresponding update request shape deliberately omits it.
    /// <summary>
    /// Gets or sets the sort position of this page among its siblings under the same
    /// parent. Maps the legacy <c>Tabs.TabOrder</c> column, <c>int NOT NULL</c> defaulting
    /// to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This value is computed by the server's page-ordering routine and is read-only from
    /// a client's perspective. The legacy read path could yield the <c>-1</c> integer
    /// sentinel through its null-substituting reader, but the terminal schema declares the
    /// column <c>NOT NULL</c> with a default of <c>0</c>, so the target type is a
    /// non-nullable integer rather than a nullable one.
    /// </remarks>
    public int TabOrder { get; set; }

    // MIGRATION: the legacy TabInfo.ParentId was a non-nullable VB Integer whose "no
    // parent" value was the in-band sentinel -1 (the legacy null-integer sentinel), even
    // though the underlying column is `ParentId int NULL`. The target models it as int?
    // with null meaning "root-level page", so a root-level page emits `"parentId": null` under
    // the configured Never ignore policy - a written null, never -1 and never a missing member.
    // That is a deliberate, documented change of external
    // representation rather than an accident of serialisation. Absence must be tested with
    // `is null`, never with `== -1` or `<= 0`, because -1 and 0 are both legitimate
    // identifiers elsewhere in this schema.
    /// <summary>
    /// Gets or sets the identifier of this page's parent page, or <see langword="null"/>
    /// when the page sits at the root of the hierarchy. Maps the legacy
    /// <c>Tabs.ParentId</c> column, <c>int NULL</c>. Captioned "Parent Page" in the legacy
    /// administration screens, where the root choice was labelled "Root".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy type was a non-nullable integer that used <c>-1</c> to mean "no parent".
    /// The target representation is a nullable integer in which <see langword="null"/>
    /// means "root-level page". Serialisation is configured once for the whole application with the
    /// <c>Never</c> ignore condition (<c>ServiceCollectionExtensions.cs</c>, stated on both the
    /// minimal-API and controller surfaces), so a root-level page emits this property as an explicit
    /// <c>"parentId": null</c> rather than omitting it, and it is never emitted as <c>-1</c>.
    /// </para>
    /// <para>
    /// Test for a root-level page with <c>ParentId is null</c>. Never write
    /// <c>ParentId == -1</c> or <c>ParentId &lt;= 0</c> as an absence check: <c>0</c> is a
    /// legitimate page identifier under <c>IDENTITY(0, 1)</c>, <c>-1</c> is a legitimate
    /// portal identifier under <c>IDENTITY(-1, 1)</c>, and the legacy code additionally
    /// used <c>-2</c> for a page detached from the tree and <c>int.MinValue</c> for
    /// "any parent".
    /// </para>
    /// <para>
    /// The legacy help text for the corresponding editor field reads: "Select the page
    /// that you would like this page to be a child of."
    /// </para>
    /// </remarks>
    public int? ParentId { get; set; }

    // MIGRATION: Level is server-derived, not client-supplied. The legacy update path
    // accepted no level argument: the page-ordering routine was called with 0 for both
    // level and order so that it would recompute the whole subtree itself. It is surfaced
    // on this row purely so a client can render an indented tree without a second
    // request, and the corresponding update request shape deliberately omits it.
    /// <summary>
    /// Gets or sets the depth of this page in the hierarchy, where <c>0</c> denotes a
    /// root-level page. Maps the legacy <c>Tabs.Level</c> column, <c>int NOT NULL</c>
    /// defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This value is derived by the server from the parent chain and is read-only from a
    /// client's perspective. It exists on this row only so that a tree or indented list can
    /// be rendered from a single response, without the client having to walk the hierarchy
    /// or issue further requests. As with the sort position, the legacy reader could yield
    /// the <c>-1</c> integer sentinel, but the terminal schema declares the column
    /// <c>NOT NULL</c> with a default of <c>0</c>, so the target type is a non-nullable
    /// integer.
    /// </remarks>
    public int Level { get; set; }

    // MIGRATION: TabPath is server-derived. The legacy code assigned it exclusively by
    // calling GenerateTabPath(ParentId, TabName) — at four distinct sites, three in the
    // page controller and one in the page editor code-behind — so it was never authored
    // by hand and never accepted from the client. It is a materialised denormalisation of
    // the parent chain, exposed here for display and matching only; the corresponding
    // update request shape deliberately omits it.
    /// <summary>
    /// Gets or sets the materialised hierarchical path of this page. Maps the legacy
    /// <c>Tabs.TabPath</c> column, <c>nvarchar(255) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The value is generated by the server from the parent chain and the page name, and
    /// is read-only from a client's perspective. The legacy representation of "no path" was
    /// the empty string rather than <c>null</c>, because the legacy null-string sentinel
    /// was <c>""</c>; the target representation is a nullable string, and a mapper must not
    /// silently convert between the two in either direction. The maximum length of 255
    /// characters is documentation only.
    /// </remarks>
    public string? TabPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page appears in the navigation menu.
    /// Captioned "Include In Menu" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.IsVisible</c> column, <c>bit NOT NULL</c> defaulting to <c>1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member name is inherited from the schema, but it is not a general-purpose
    /// visibility flag and should not be read as one: it controls menu inclusion only. The
    /// legacy caption was "Include In Menu" and the legacy help text read "You have the
    /// choice on whether or not to include the page in the main navigation menu." A page
    /// with this flag clear is still reachable by direct link.
    /// </para>
    /// <para>
    /// The value is exposed rather than filtered. The legacy page-management list
    /// deliberately <em>included</em> menu-excluded pages — it requested them explicitly —
    /// so the decision about which rows to present belongs to the caller, and the caller
    /// needs this flag in order to make it.
    /// </para>
    /// </remarks>
    public bool IsVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page is disabled. Captioned "Disabled"
    /// in the legacy administration screens. Maps the legacy <c>Tabs.DisableLink</c>
    /// column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy help text reads "If the page is disabled it is not available to users of
    /// the site." A disabled page typically still renders in the menu as inert text rather
    /// than as a link.
    /// </para>
    /// <para>
    /// Known legacy behaviour, preserved and not corrected. The legacy page editor assigned
    /// this flag only when the page being edited was not one of five protected system pages
    /// — the admin, splash, home, login and user pages. For those five the posted value was
    /// silently discarded and the flag retained its default of <see langword="false"/>, with
    /// no message shown to the administrator. That behaviour is recorded here rather than
    /// repaired, because behavioural equivalence takes precedence over opportunistic
    /// correction; any change would be a deliberate, separately documented decision.
    /// </para>
    /// </remarks>
    public bool DisableLink { get; set; }

    // MIGRATION: IsDeleted is exposed on this row, never filtered or hidden at the DTO
    // level — the endpoint or service decides which rows to return. A soft-deleted page is
    // still a row: the legacy recycle-bin screen enumerated exactly these rows. Note the
    // legacy page-management list applied an asymmetric filter, EXCLUDING deleted pages
    // while INCLUDING menu-excluded ones, which is precisely why both this flag and
    // IsVisible must travel on the row: the caller owns the filtering decision and needs
    // the data to make it.
    /// <summary>
    /// Gets or sets a value indicating whether this page has been soft-deleted into the
    /// recycle bin. Maps the legacy <c>Tabs.IsDeleted</c> column, <c>bit NOT NULL</c>
    /// defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// A soft-deleted page remains a row in the table; deletion is a flag, not a removal,
    /// and the legacy recycle-bin screen listed pages by testing exactly this flag. The
    /// value is therefore surfaced rather than suppressed. Filtering is the responsibility
    /// of the service or endpoint, not of this projection: the legacy page-management list
    /// excluded soft-deleted pages while including menu-excluded ones, and reproducing that
    /// asymmetry requires the caller to see both flags.
    /// </remarks>
    public bool IsDeleted { get; set; }

    // MIGRATION: HasChildren is a computed projection, not a persisted column — it is
    // absent from the Tabs table and from the Tab domain entity, and is populated by the
    // repository or service. The legacy terminal read view produced it with
    // `CASE WHEN EXISTS (SELECT 1 FROM Tabs T2 WHERE T2.ParentId = T.TabId)
    //  THEN 'true' ELSE 'false' END`, which returns the STRINGS 'true'/'false' rather than
    // a bit — which is exactly why the legacy reader wrapped it in Convert.ToBoolean. This
    // DTO changes the external representation to a genuine JSON boolean.
    /// <summary>
    /// Gets or sets a value indicating whether any other page names this page as its
    /// parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a persisted column and not a member of the page domain entity. It is a
    /// computed existence projection, populated by the repository or the application
    /// service, and it exists so that a client can decide whether to render an expander on
    /// a hierarchy node without issuing a second request per row.
    /// </para>
    /// <para>
    /// The legacy read view computed the same fact but emitted it as the string
    /// <c>'true'</c> or <c>'false'</c>, which the legacy reader then coerced with a boolean
    /// conversion. The target contract is a real boolean and serialises as JSON
    /// <c>true</c>/<c>false</c>, never as a quoted string. Consumers must not parse it as
    /// text.
    /// </para>
    /// </remarks>
    public bool HasChildren { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page requires a secure connection.
    /// Captioned "Secure?" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.IsSecure</c> column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This is the newest column on the page table: it was added by the terminal upgrade
    /// script in the legacy schema chain, together with the final revision of the page read
    /// view, so installations upgraded from an earlier version acquire it with a default of
    /// <c>0</c> rather than with a per-page decision.
    /// </remarks>
    public bool IsSecure { get; set; }

    /// <summary>
    /// Gets or sets the navigation target used when this page acts as a link to another
    /// resource rather than hosting content of its own. Captioned "Link Url" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.Url</c> column,
    /// <c>nvarchar(255) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "If you would like this page to behave as a navigation
    /// link to another resource, you can specify the Link URL value here". Note that the
    /// legacy read view projected this column under the upper-case alias <c>URL</c> while
    /// the base table spells it <c>Url</c>; the target contract uses the single spelling
    /// declared here. The legacy value for "not a link" was the empty string rather than
    /// <c>null</c>, since the legacy null-string sentinel was <c>""</c>; the target
    /// representation is a nullable string and a mapper must not silently convert between
    /// the two. The maximum length of 255 characters is documentation only.
    /// </remarks>
    public string? Url { get; set; }

    // MIGRATION: IconFile carries whatever the mapper supplies, which may be a raw
    // `fileid=NNN` token rather than a usable path. The base table stores the raw value;
    // only the legacy read view resolved it, with
    // `CASE WHEN LEFT(LOWER(T.IconFile), 6) = 'fileid' THEN (SELECT Folder + FileName FROM
    //  Files WHERE 'fileid=' + convert(varchar, Files.FileID) = T.IconFile)
    //  ELSE T.IconFile END`.
    // The domain entity intentionally stores the raw base-table value and defers
    // resolution to the repository/mapper layer. Resolution must NOT be attempted here: it
    // requires a file lookup, and a property getter on a DTO may not perform input or
    // output.
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
    /// value that may be either a path or a <c>fileid=NNN</c> token; only the legacy read
    /// view resolved the token form into a folder-and-file-name path. Resolving it is the
    /// responsibility of the repository or the mapper, not of this type, because resolution
    /// requires a file lookup and no member of a DTO may perform input or output. Consumers
    /// must therefore tolerate an unresolved token unless the supplying mapper documents
    /// that it resolves them.
    /// </para>
    /// <para>
    /// The legacy value for "no icon" was the empty string rather than <c>null</c>, since
    /// the legacy null-string sentinel was <c>""</c>; the target representation is a
    /// nullable string and a mapper must not silently convert between the two. The maximum
    /// length of 100 characters is documentation only.
    /// </para>
    /// </remarks>
    public string? IconFile { get; set; }
}
