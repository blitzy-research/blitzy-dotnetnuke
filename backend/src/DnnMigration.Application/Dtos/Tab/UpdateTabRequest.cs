namespace DnnMigration.Application.Dtos.Tab;

// MIGRATION: the page identifier is route-supplied, never body-supplied. The legacy editor carried
// the identity in page state - Website/admin/Tabs/ManageTabs.ascx.vb line 251 assigns
// objTab.TabID = TabId from the page's own context, and no form field ever contributed it. The
// target takes it from the route template PUT /api/v1/tabs/{id}, so this request declares no
// identifier member at all. The route value is authoritative. Carrying a second copy in the body
// would create a mismatch hazard - two sources of truth for which page is being written - for no
// gain, because the route already names the resource unambiguously.

// MIGRATION: TabPath, TabOrder and Level are server-derived and are therefore absent, even though
// the terminal update procedure does persist TabPath. Each exclusion is measured, not assumed.
// TabPath was assigned exclusively by GenerateTabPath(ParentId, TabName) at four distinct sites -
// Library/Components/Tabs/TabController.vb lines 312, 333 and 817, and ManageTabs.ascx.vb line 269 -
// so it was computed immediately before the write and never authored by hand. It also cascaded:
// TabController.vb line 783 set updateChildren when the name or the parent changed, and lines 809
// to 811 then recursively regenerated the path of every descendant through UpdateChildTabPath. That
// cascade is page-service business logic, emphatically not DTO logic. TabOrder and Level are not
// written by the update procedure at all: its parameter list omits both and its UPDATE body sets
// neither, and TabController.vb line 787 called
// UpdatePortalTabOrder(PortalID, TabID, ParentId, 0, 0, IsVisible) - passing 0 for the level and 0
// for the order - precisely so that the ordering routine would recompute them. A separate write
// path existed for them, TabController.vb line 818
// UpdateTabOrder(TabID, TabOrder, Level, ParentId, TabPath), and the narrow endpoint surface does
// not expose it. Accepting any of the three from a client would permit a hierarchy that
// contradicts its own denormalised description.

// MIGRATION: a plan citation is corrected here rather than obeyed literally, and the correction is
// recorded so it is not silently reversed. The plan says of the page service that the
// "Optional ByVal parameters [L243, L550] become explicit overloads or request-object properties".
// Read naively that would add both optional tails to this request. Measurement shows the line
// references are accurate but the conclusion does not reach this contract. TabController.vb line
// 243 is Private Sub MoveTab(..., Optional ByVal blnAddChild As Boolean = True) - a PRIVATE
// tree-reordering helper that belongs to no publicly reachable contract, so its tail cannot become
// a request property. TabController.vb line 550 is
// Public Sub UpdatePortalTabOrder(..., Optional ByVal NewTab As Boolean = False) - a hierarchy
// ordering and move operation, and the narrow endpoint surface exposes no reorder or move endpoint
// for it to serve. Those two are the ONLY Optional ByVal sites in the whole of that 1,302-line
// file, and neither belongs to PUT /api/v1/tabs/{id}. Consequently this request declares no
// reorder or move member of any kind: no new-tab flag, no add-child flag, no new-parent, new-order,
// new-level, move-direction or target-index member. Nor is there any ByRef conversion to perform
// for pages: the only ByRef in the entire page tree is the token accessor at
// Library/Components/Tabs/TabInfo.vb line 524, which is dropped along with the excluded
// token-replacement interface, and no out or ref parameter appears in any target public-facing API
// regardless.

/// <summary>
/// The client-settable state of one page, submitted as the request body of
/// <c>PUT /api/v1/tabs/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Purpose. This type is the sole write contract for a page. It is an inbound shape only:
/// it is never returned by an endpoint, it carries no paging metadata, and it references no
/// envelope type. Every member is applied as supplied, so the request is a complete
/// replacement of the editable subset rather than a patch - a caller reads the page, changes
/// what it means to change, and submits the whole object. That mirrors the legacy screen,
/// which posted its entire form on every save.
/// </para>
/// <para>
/// Seventeen members, derived twice and independently. The count is not a matter of taste;
/// two separate measurements agree on it. First, the terminal <c>UpdateTab</c> stored
/// procedure, defined at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.04.SqlDataProvider</c>, declares
/// nineteen parameters - one key plus <em>eighteen</em> mutable fields - and its <c>UPDATE</c>
/// body sets exactly those eighteen columns <c>where TabId = @TabId</c>. Removing the one
/// server-derived field among them, the materialised hierarchy path, leaves seventeen. Second,
/// the legacy page-settings form <c>Website/admin/Tabs/managetabs.ascx</c> carries exactly
/// <em>sixteen</em> page fields; adding the recycle-bin flag, for the reason given below,
/// leaves seventeen. The two derivations reconcile, and that agreement is the justification
/// for this shape.
/// </para>
/// <para>
/// The procedure's <c>UPDATE</c> body touches neither the portal, the sibling order nor the
/// depth. That was measured in the terminal script rather than inferred, and it is the direct
/// reason none of the three appears here.
/// </para>
/// <para>
/// The endpoint surface is deliberately narrow, and this shape is bounded by it. The page
/// endpoints are <c>GET /api/v1/portals/{id}/tabs</c> and <c>GET</c> plus <c>PUT</c> on
/// <c>/api/v1/tabs/{id}</c> - nothing else. There is no create endpoint, no delete endpoint,
/// no reorder endpoint and no move endpoint, so no create, delete, restore, purge, reorder or
/// move request shape exists anywhere in this folder, and none may be added. The legacy create
/// path is genuinely a different shape - the terminal <c>AddTab</c> procedure takes a portal
/// argument, takes no page identifier, hard-codes the recycle-bin flag to the literal <c>0</c>
/// and returns a generated identity - so inventing one here would be guesswork rather than
/// migration.
/// </para>
/// <para>
/// Members excluded because the server owns them. The page identifier is taken from the route
/// and so is absent from the body. The portal identifier is absent because the terminal update
/// procedure accepts no portal argument at all: a page cannot be moved between tenants through
/// this path, and the legacy editor never offered the choice, taking the value from ambient
/// page context at <c>ManageTabs.ascx.vb</c> line 252 rather than from a form field. The
/// sibling order, the depth and the materialised path are absent because the server recomputes
/// them - see the migration notes above this type. The child-existence flag is absent because
/// it is not a column at all but an existence projection in the terminal read view, and
/// nothing can write it back. Confirming all of this from a third direction: there is no
/// control for the order, the depth, the path, the child flag or the recycle-bin flag anywhere
/// on the legacy form.
/// </para>
/// <para>
/// Three page shapes, and the portal identifier is asymmetric across them on purpose. Each of
/// the three is an independent, flat type: none derives from another, none references another,
/// and they must not be "consolidated". The list-row shape carries fourteen members and is the
/// element of the portal-scoped list; it omits the portal identifier because its own route
/// already names the portal. The detail shape carries twenty-three members and is the complete
/// read surface for one page; it <em>includes</em> the portal identifier, because
/// <c>GET /api/v1/tabs/{id}</c> is not portal-scoped by its route and a caller could not
/// otherwise learn which tenant owns the page. This request carries seventeen and
/// <em>omits</em> it, because the write path cannot change it. A later reader must not
/// "harmonise" that three-way difference away; each position is load-bearing.
/// </para>
/// <para>
/// Members excluded because the legacy column no longer exists. Two role-string columns were
/// physically dropped in the 3.0 upgrade and thereafter recomputed from permissions at read
/// time, and four further columns - two mobile-presentation, two pane-width - were dropped
/// outright in the 2.0 upgrade despite appearing in the original create-table statement. Never
/// derive this shape from the baseline script: the page table has a forty-one step alteration
/// history, and only the cumulative terminal state is meaningful. Permissions are a separate
/// concern served by the read-only permission catalogue, so the legacy permission collection
/// is likewise absent even though the legacy form did carry a permissions grid.
/// </para>
/// <para>
/// No audit or concurrency members, because no such column exists. Across all eighty-eight
/// upgrade scripts the creator, creation-date, last-modifier and last-modified-date column
/// names do not occur even once, and the page table carries no created-date column. The page
/// domain entity derives from the plain entity base rather than the auditable one for exactly
/// that reason. There is likewise no row version, entity tag or concurrency stamp: the legacy
/// schema has none, and the schema is immutable to this migration, so one cannot be introduced
/// here.
/// </para>
/// <para>
/// Magic integers: never test an identifier for "absence". Five distinct magic values are live
/// in the legacy page domain, and two of them collide outright with real persisted data:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>-1</c> was the legacy null-integer sentinel - verified literally as <c>Return -1</c>
///     in <c>Library/Components/Shared/Null.vb</c> - standing for "no parent", "unknown
///     portal" or "host page". Yet <c>-1</c> is simultaneously a legitimate portal
///     identifier, because the portal table's key is declared <c>IDENTITY(-1, 1)</c>.
///   </description></item>
///   <item><description>
///     <c>-2</c> meant "detached from the tree" while a page was being deleted or moved.
///   </description></item>
///   <item><description>
///     <c>0</c> is a legitimate page identifier, because the page table's key is declared
///     <c>IDENTITY(0, 1)</c> - and <c>0</c> was also the "recompute this for me" reset the
///     legacy code passed for the level and order arguments.
///   </description></item>
///   <item><description>
///     <c>int.MinValue</c> meant "any parent" in the legacy lookup-by-name path.
///   </description></item>
///   <item><description>
///     <c>";"</c> meant "no roles" in the legacy permission strings.
///   </description></item>
/// </list>
/// <para>
/// Consequently, never compare a member of this type against <c>0</c>, <c>-1</c>, <c>-2</c>,
/// <c>default</c> or <c>int.MinValue</c> to decide whether a value was supplied. Test the
/// nullable members with <c>is null</c> instead. This is also why the domain entity base type
/// exposes no bare identity member and no "is new" or "is transient" helper:
/// <c>default(int)</c> is <c>0</c>, and <c>0</c> is a real stored identity for pages, roles
/// and modules alike.
/// </para>
/// <para>
/// Sentinel boundary. The legacy code encoded absence as in-band values - <c>-1</c> for a
/// missing integer, <c>DateTime.MinValue</c> for a missing date and, counter-intuitively, the
/// <em>empty string</em> rather than <c>null</c> for a missing string - and converted every
/// database <c>NULL</c> into one of them on read. This contract uses honest nullable types and
/// pins the resulting wire representation explicitly, member by member, below. A client
/// expressing absence sends JSON <c>null</c>; it must never send <c>-1</c>, <c>0</c> or
/// <c>0001-01-01</c> and expect them to be understood as "not set". Equally, a mapper reading
/// this type must not quietly rewrite <c>""</c> to <c>null</c> or <c>null</c> to <c>""</c> in
/// either direction.
/// </para>
/// <para>
/// Validation lives in the page service, not on this type. No page request validator exists in
/// this solution - the validation folder declares ten validators and none of them is for a
/// page - so this type carries no validation attribute and no declarative rule of any kind.
/// The legacy rules are nevertheless recorded in the member documentation below so that the
/// page service can enforce them and so that the requirement for matching validation rules and
/// equivalent error messages stays satisfiable. Those rules are: the page name is required and
/// must not be a reserved device name; the derived path must be unique within the portal, a
/// check the legacy screen applied on add only; a page may not be reparented onto itself or
/// onto one of its own descendants; and five system pages - the administration, splash, home,
/// login and user pages - are protected against deletion, refused with the message "A page
/// defined as Home, Splash, Login or User Page for the portal, or the last visible Page in the
/// portal cannot be deleted." The update path is also audited, as the legacy screen logged a
/// page-updated event and the legacy controller logged a page-sent-to-recycle-bin event; that
/// audit is emitted as a structured log event by the service. This type logs nothing, computes
/// nothing and validates nothing.
/// </para>
/// <para>
/// Inertness. Every member is a trivial auto-property. This type declares no method of any
/// kind - no validate, no map, no apply, no normalise, no trim helper - performs no input or
/// output, walks no hierarchy, and resolves no file. Mapping to the domain entity lives in the
/// page mapper. The legacy counter-example is instructive: the legacy page entity's read-only
/// administration-page test at <c>Library/Components/Tabs/TabInfo.vb</c> lines 435 to 464 read
/// a cache, then a portal controller, then the database, all from inside a property getter.
/// That member is deliberately not carried forward, and nothing here may follow its pattern.
/// </para>
/// <para>
/// Serialisation. Property names are bound and emitted in camel case by the API layer's
/// central JSON configuration, yielding <c>tabName</c>, <c>parentId</c>, <c>keywords</c>,
/// <c>skinSrc</c>, <c>containerSrc</c>, <c>pageHeadText</c> and <c>isDeleted</c>. No
/// serialisation attribute is declared: the legacy XML serialisation attributes on the legacy
/// page entity, including its root element declaration, and the legacy token-accessor
/// interface it implemented are all dropped rather than translated. The type is a plain sealed
/// class with mutable auto-properties and an implicit parameterless constructor, because model
/// binding, the mappers and the JSON serialiser all construct it that way.
/// </para>
/// <para>
/// Terminology: "Page", not "Tab". The type name retains the legacy <c>Tab</c> vocabulary so
/// that it lines up with the database schema and the page domain entity, but the concept an
/// administrator actually sees is a <em>Page</em>. The legacy resource files are explicit: the
/// edit screen is titled "Edit Page" and the field captions are "Page Name", "Page Title" and
/// "Parent Page". Client-facing labels should therefore say "Page" even though the wire
/// contract says "tab".
/// </para>
/// </remarks>
public sealed class UpdateTabRequest
{
    // MIGRATION: initialised rather than left to the global uninitialised-property suppression. That
    // suppression exists for entities materialised by the ORM behind a NOT NULL column that rejects a
    // null loudly; this type is materialised by the model binder from a request body, which has no
    // such backstop, so a body omitting the field would otherwise present a null through a
    // non-nullable contract and fault the reserved-name check downstream. The sibling detail
    // projection initialises its own copy of this member for the same reason, so the pair stays
    // coherent. An empty name is still a validation failure - see the rules below - it is simply
    // reported as one instead of throwing.
    /// <summary>
    /// Gets or sets the page's name as it appears in the navigation menu. Captioned "Page Name" in
    /// the legacy administration screens. Maps the legacy <c>Tabs.TabName</c> column,
    /// <c>nvarchar(50) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "This is the name of the Page. The text you enter will be displayed in the
    /// menu system."
    /// </para>
    /// <para>
    /// Required, maximum length 50. The legacy form declared a required-field validator whose message
    /// was "Page Name Is Required".
    /// </para>
    /// <para>
    /// Reserved device names are rejected. The legacy screen matched the name against the pattern
    /// <c>^AUX$|^CON$|^LPT[1-9]$|^CON$|^COM[1-9]$|^NUL$</c>, case-insensitively, and refused it with
    /// "This is an invalid Page Name". The <c>^CON$</c> alternative genuinely appears twice in the
    /// legacy pattern; that redundancy is harmless and is reproduced faithfully rather than tidied,
    /// because behavioural equivalence takes precedence over cosmetic correction.
    /// </para>
    /// <para>
    /// Uniqueness is a derived-path rule, and its scope on update is an open question the service
    /// must settle. The legacy screen tested the <em>derived path</em> for collision within the
    /// portal and refused a duplicate with "The Page Name you chose is already being used for another
    /// page at the same level of the page heirarchy." - the misspelling is the legacy wording,
    /// reproduced verbatim for message parity. That check ran on <em>add only</em>. Because the path
    /// is derived from the parent and this name, a rename or a reparent through this endpoint can
    /// collide too, so the page service must decide whether to extend the check to updates. The
    /// question is recorded here deliberately and is not resolved by this contract, which carries no
    /// member for it.
    /// </para>
    /// <para>
    /// Changing this name cascades: the legacy controller regenerated the materialised path of this
    /// page and, recursively, of every descendant. That is service behaviour, not DTO behaviour.
    /// </para>
    /// <para>
    /// Every length quoted on this type is documentation only. No validation attribute is declared
    /// anywhere here.
    /// </para>
    /// </remarks>
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the page title rendered in the browser window title. Captioned "Page Title" in
    /// the legacy administration screens. Maps the legacy <c>Tabs.Title</c> column,
    /// <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "Enter a title for the Page here. The text you enter will be displayed in
    /// the browser window title." Maximum length 200. The legacy entity sentinel-initialised this
    /// field to the empty string rather than to a null, so a client clearing the title may send
    /// either <c>null</c> or <c>""</c>; neither is rewritten into the other on the way through.
    /// </remarks>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the page description used in page metadata. Captioned "Description" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.Description</c> column,
    /// <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "Enter a description about this page here." Maximum length 500. As with the
    /// other optional text members, the legacy sentinel for absence was the empty string rather than
    /// a null.
    /// </remarks>
    public string? Description { get; set; }

    // MIGRATION: the property is spelled Keywords; the database column and the stored-procedure
    // parameter are both spelled KeyWords, with a capital W. The bridge is deliberate: the wire
    // contract and the domain-facing name use conventional English casing while the persistence layer
    // keeps the legacy spelling, and the mapper crosses between the two explicitly. Collapsing either
    // side onto the other would silently lose the value, so neither spelling may be "corrected".
    /// <summary>
    /// Gets or sets the comma-separated keywords used in page metadata. Captioned "Keywords" in the
    /// legacy administration screens. Maps the legacy <c>Tabs.KeyWords</c> column - note the capital
    /// W in the column name - <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "Enter some keywords for this page (separated by commas). These keywords are
    /// used by search engines to help index your site's pages." Maximum length 500. The legacy form
    /// applied no format validation to this field, and none is introduced here.
    /// </remarks>
    public string? Keywords { get; set; }

    // MIGRATION: a non-nullable legacy Integer whose sentinel was -1 becomes a genuine int?.
    // Library/Components/Tabs/TabInfo.vb line 156 declares ParentId As Integer, and the legacy
    // constructor sentinel-initialised it to Null.NullInteger, which is literally -1. The column has
    // always been ParentId int NULL and the domain entity is int?, so the schema and the entity win
    // the conflict. The wire form is therefore explicit and must be stated plainly: a client asking
    // for a root-level page sends parentId: null, NOT -1 and NOT 0. Both of those are real
    // identifiers in this schema. The legacy parent picker did carry the literal -1 on its "Root"
    // option - ManageTabs.ascx.vb line 262 parsed the selected value straight into the field - and
    // that sentinel is deliberately NOT carried forward into the contract, because sentinels survive
    // at the boundary only where the legacy value is externally observable, and here the honest null
    // is unambiguous while -1 is not. Never test this member with ParentId <= 0 or ParentId == -1 to
    // decide absence; use ParentId is null.
    /// <summary>
    /// Gets or sets the identifier of the page this page hangs beneath, or <see langword="null"/> to
    /// make it a root-level page. Captioned "Parent Page" in the legacy administration screens. Maps
    /// the legacy <c>Tabs.ParentId</c> column, <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "Select the page that you would like this page to be a child of."
    /// </para>
    /// <para>
    /// <see langword="null"/> means "make this a root-level page". It does not mean "leave the parent
    /// as it is" - this request is a complete replacement of the editable subset, so an omitted parent
    /// is an instruction to move the page to the root. Because the page key is seeded at <c>0</c>,
    /// a submitted <c>0</c> names a real parent page and must never be read as "no parent".
    /// </para>
    /// <para>
    /// Changing this moves the page and every descendant with it, and the server recomputes the depth,
    /// the sibling order and the materialised path of the affected subtree afterwards. None of those
    /// three is accepted from a client.
    /// </para>
    /// <para>
    /// Cycles are rejected by the page service. The legacy screen guarded reparenting with a
    /// self-parent test and a recursive ancestry walk at <c>ManageTabs.ascx.vb</c> lines 304 to 310.
    /// A discovered legacy defect is recorded here and deliberately not fixed in this contract: when
    /// either condition tripped, the legacy screen simply skipped the update and displayed <em>no
    /// error at all</em>, a silent no-op a user could easily mistake for success. The condition
    /// itself is preserved; how the failure is surfaced is a service concern, and this DTO neither
    /// detects nor reports it.
    /// </para>
    /// <para>
    /// A parent belonging to a different portal is also rejected by the service. That check matters
    /// more in the target than it did in the legacy screen, because the identifier now arrives in a
    /// request body rather than from a portal-filtered picker.
    /// </para>
    /// </remarks>
    public int? ParentId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page is included in the navigation menu. Captioned
    /// "Include In Menu" in the legacy administration screens - it is a menu-inclusion flag, not a
    /// general-purpose visibility or publication switch. Maps the legacy <c>Tabs.IsVisible</c> column,
    /// <c>bit NOT NULL</c> defaulting to <c>1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "You have the choice on whether or not to include the page in the main
    /// navigation menu. If a page is not included in the menu, you can still link to it based on its
    /// page URL." Excluding a page from the menu therefore does not restrict access to it; that is
    /// what the permission model and the disabled flag are for.
    /// </para>
    /// <para>
    /// No initialiser is declared, so an omitted field binds to <see langword="false"/>. That is
    /// intentional for a complete-replacement write: the column default of <c>1</c> governs the
    /// creation of a row, which this endpoint cannot do, and inventing a default here would let an
    /// omitted field silently contradict the submitted document. A caller that means "keep it in the
    /// menu" must say so.
    /// </para>
    /// <para>
    /// The legacy ordering routine was passed this flag alongside the recompute reset, because a page
    /// hidden from the menu still occupies a position among its siblings. Ordering remains entirely
    /// server-owned.
    /// </para>
    /// </remarks>
    public bool IsVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page's navigation entry is inert - present in the
    /// menu but not navigable. Captioned "Disabled" in the legacy administration screens. Maps the
    /// legacy <c>Tabs.DisableLink</c> column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "If the page is disabled it is not available to users of the site. You can
    /// use this option to suppress content that you might wish to show at a later time." The typical
    /// use is a heading that groups child pages without being a destination itself.
    /// </para>
    /// <para>
    /// A discovered legacy defect is recorded here and deliberately not fixed. At
    /// <c>ManageTabs.ascx.vb</c> lines 258 to 260 the legacy save assigned this field only when the
    /// page was <em>not</em> one of the five protected system pages - the administration, splash,
    /// home, login and user pages. For those five the submitted checkbox value was silently
    /// discarded, with no message and no indication to the user that the field had been ignored. The
    /// behaviour is preserved in intent as a page-service concern; this contract simply carries the
    /// submitted value and suppresses nothing, and it is the service that decides what to honour and
    /// how to report a refusal.
    /// </para>
    /// </remarks>
    public bool DisableLink { get; set; }

    // MIGRATION: the icon value is accepted and stored verbatim, and may legitimately be a raw
    // file-reference token rather than a path. The base table stores whatever the legacy icon picker
    // produced - ManageTabs.ascx.vb line 245 read the picker's URL and line 263 assigned it unchanged
    // - and it was the terminal read view, not the write path, that resolved a "fileid=" prefix into a
    // folder-and-filename by joining the files table. The domain entity likewise stores the raw value.
    // Consequently this request performs no resolution, no file lookup, no existence check and no
    // validation of the reference. A file lookup inside a property getter would be exactly the kind of
    // input/output in a DTO that is forbidden outright, and the legacy page entity's
    // administration-page getter is the cautionary precedent.
    /// <summary>
    /// Gets or sets the reference to the icon shown beside this page in the menu. Captioned "Icon" in
    /// the legacy administration screens. Maps the legacy <c>Tabs.IconFile</c> column,
    /// <c>nvarchar(100) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "You can choose an icon that can be used in the menu." Maximum length 100.
    /// The value is opaque to this contract: it may be a path or a raw file-reference token of the
    /// form <c>fileid=NNN</c>, and it is stored exactly as submitted. Resolution of a token into a
    /// displayable path happens on the read side, never here.
    /// </remarks>
    public string? IconFile { get; set; }

    // MIGRATION: NO SKIN SOURCE AND NO CONTAINER SOURCE ON THIS CONTRACT, AND THAT IS THE SCOPE
    // DECISION RATHER THAN AN OVERSIGHT. Both were previously declared here - Tabs.SkinSrc and
    // Tabs.ContainerSrc, each nvarchar(200) NULL and each accepted by the terminal update procedure -
    // on the argument that omitting a column the legacy screen posted would lose write parity. That
    // argument is subordinate to an explicit exclusion: skinning and containers are excluded from this
    // migration wholesale, and a WRITABLE member is not an inert one. Declaring them made this endpoint
    // the supported way to change a page's skin and container, published them in the OpenAPI document
    // as part of the page-edit contract, and gave them validation rules - which is precisely the
    // subsystem the exclusion removes, re-entered through the write surface.
    //
    // NOTHING STORED IS LOST BY THE REMOVAL, and that is what makes it safe. The projection in
    // Mapping/TabMappings.cs no longer assigns either column, so an update leaves both exactly as they
    // were rather than writing an absent value over an administrator's stored choice - which is the
    // stronger guarantee of the two, because a caller that simply omitted the member from its JSON
    // previously BLANKED the column. The values remain readable on TabDetailDto, so a stored choice is
    // still observable; it is only no longer settable through this API. Changing a skin or a container
    // is a skinning operation, and skinning has no endpoint in this migration.
    /// <summary>
    /// Gets or sets the target that turns this page into a navigation link to another resource, or
    /// <see langword="null"/> for an ordinary page. Captioned "Link Url" in the legacy administration
    /// screens. Maps the legacy <c>Tabs.Url</c> column, <c>nvarchar(255) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "If you would like this page to behave as a navigation link to another
    /// resource, you can specify the Link URL value here. Please note that this field is optional."
    /// Maximum length 255. The URL control produced one of three persisted forms: a numeric page
    /// identifier, a <c>fileid=NNN</c> token, or an absolute external URI. The request validator
    /// allowlists those forms and restricts absolute URIs to HTTP, HTTPS and mailto, because the value is
    /// stored and later returned to navigation consumers; active schemes such as <c>javascript:</c> and
    /// <c>data:</c> are never accepted. The admitted value is stored verbatim and is not fetched or
    /// dereferenced during validation.
    /// </remarks>
    public string? Url { get; set; }

    // MIGRATION: a non-nullable legacy Date whose sentinel was DateTime.MinValue becomes a genuine
    // DateTime?. Library/Components/Tabs/TabInfo.vb line 264 declares StartDate As Date, and the legacy
    // constructor sentinel-initialised it to Null.NullDate, which is Date.MinValue. The column is
    // datetime NULL and the domain entity is DateTime?, so the schema and the entity win. The sentinel
    // really was written on the way in, not merely read: ManageTabs.ascx.vb lines 288 to 292 assigned
    // Null.NullDate whenever the start-date textbox was empty. The wire form is therefore explicit - a
    // client clearing the start date sends null, NEVER 0001-01-01 - and a mapper must not translate
    // between the two.
    /// <summary>
    /// Gets or sets the moment from which the page becomes available, or <see langword="null"/> for no
    /// start restriction. Captioned "Start Date:" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.StartDate</c> column, <c>datetime NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "Enter the start date for displaying this page. You may use the Calendar to
    /// pick a date."
    /// </para>
    /// <para>
    /// <see langword="null"/> means "no start restriction". The legacy screen validated only that the
    /// text entered was a parseable date - its validator performed a data-type check and nothing more,
    /// reporting "Invalid Start Date" on failure - and that concern disappears entirely in a typed
    /// contract, where a malformed value fails deserialisation before any rule could run.
    /// </para>
    /// </remarks>
    public DateTime? StartDate { get; set; }

    // MIGRATION: the same Date-to-nullable-date conversion as the start date above, on the same
    // evidence - TabInfo.vb line 273 declares EndDate As Date, sentinel-initialised to Null.NullDate,
    // against a datetime NULL column, and ManageTabs.ascx.vb lines 293 to 297 wrote the sentinel
    // whenever the end-date textbox was empty. A client clearing the end date sends null, never
    // 0001-01-01.
    //
    // MIGRATION: there was NO end-date-after-start-date validation in the legacy application, and none
    // is added here - not as an attribute, not as code, and not as documentation implying one is
    // enforced. This is recorded explicitly so that a later reviewer does not add the rule believing it
    // was simply overlooked. The two legacy comparison validators, on the start-date and end-date
    // textboxes respectively, both declared a data-type-check operator: each validated the FORMAT of
    // its own field in isolation and neither compared one field against the other. Adding a
    // cross-field rule would be opportunistic hardening, which the domain-logic-preservation directive
    // forbids, and it would reject page configurations the legacy application accepted.
    /// <summary>
    /// Gets or sets the moment at which the page stops being available, or <see langword="null"/> for
    /// no end restriction. Captioned "End Date:" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.EndDate</c> column, <c>datetime NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "Enter the end date for displaying this page. You may use the Calendar to pick
    /// a date."
    /// </para>
    /// <para>
    /// <see langword="null"/> means "no end restriction".
    /// </para>
    /// <para>
    /// <strong>No relationship between this member and the start date is validated anywhere in the
    /// target, because none was validated anywhere in the legacy application.</strong> An end date
    /// earlier than the start date is accepted and stored, exactly as it was before. The legacy
    /// validator on this field checked only that the entered text was a parseable date, reporting
    /// "Invalid End Date" on failure. Do not add a comparison rule to this contract, to a validator,
    /// or to the page service.
    /// </para>
    /// </remarks>
    public DateTime? EndDate { get; set; }

    // MIGRATION: a non-nullable legacy Integer whose sentinel was -1 becomes a genuine int?.
    // TabInfo.vb line 300 declares RefreshInterval As Integer, sentinel-initialised to
    // Null.NullInteger, which is -1; the column is int NULL and the domain entity is int?. Legacy -1
    // meant "no automatic refresh" and the target expresses that as null, so a client disabling refresh
    // sends null, NEVER -1 and never 0. The legacy write path corroborates the sentinel: ManageTabs
    // .ascx.vb lines 298 to 300 assigned the value ONLY when the textbox was both non-empty and
    // numeric, otherwise leaving the constructor's -1 in place - so a blank or non-numeric entry
    // silently became "no refresh" rather than an error. Note also that the legacy markup declared NO
    // validator whatsoever on this field: no required-field, no range and no numeric check. No range
    // rule or numeric rule is invented here.
    /// <summary>
    /// Gets or sets the automatic page-refresh interval <strong>in seconds</strong>, or
    /// <see langword="null"/> for no automatic refresh. Captioned "Refresh Interval (seconds)" in the
    /// legacy administration screens. Maps the legacy <c>Tabs.RefreshInterval</c> column,
    /// <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit is seconds, on the authority of the legacy caption and of the legacy help text: "Enter
    /// the interval to wait between automatic page refeshes. (Example: Enter "60" for 1 minute or
    /// leave blank to disable.)" - the misspelling is the legacy wording.
    /// </para>
    /// <para>
    /// <see langword="null"/> means "no automatic refresh", which is what leaving the legacy field
    /// blank achieved. The legacy field carried no validator of any kind, so no bound is asserted here
    /// either; whether to constrain the value is a decision for the page service and is deliberately
    /// left open rather than silently resolved by this contract.
    /// </para>
    /// </remarks>
    public int? RefreshInterval { get; set; }

    /// <summary>
    /// Gets or sets raw markup to be emitted inside the page's head element, such as meta tags.
    /// Captioned "Page Header Tags" in the legacy administration screens. Maps the legacy
    /// <c>Tabs.PageHeadText</c> column, <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "Enter any tags (i.e. META tags) that should be rendered in the "HEAD" tag of
    /// the HTML for this page." Maximum length 500. The legacy form rendered this as a multi-line text
    /// box and applied no validation to it.
    /// </para>
    /// <para>
    /// The value is raw markup, stored verbatim and never parsed, sanitised or escaped by this
    /// contract. Because it is markup authored by an administrator and intended for a document head,
    /// it is inherently a privileged field: the endpoint is guarded by the page-edit authorisation
    /// policy, and that policy - not this DTO - is what limits who may set it. Authorisation does not
    /// make the markup intrinsically safe, however. A renderer must not inject it without an explicit,
    /// narrowly-scoped sanitisation and element/attribute allowlist policy; without one it must be
    /// treated as untrusted text. No sanitisation is performed here, and none was performed by the
    /// legacy screen either.
    /// </para>
    /// </remarks>
    public string? PageHeadText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page must be served over a secure connection.
    /// Captioned "Secure?" in the legacy administration screens. Maps the legacy <c>Tabs.IsSecure</c>
    /// column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy help text: "Specify whether or not this page should be forced to use a secure connection
    /// (SSL). This option will only be enabled if the administrator has Enabled SSL in the site
    /// settings."
    /// </para>
    /// <para>
    /// This column exists <em>only</em> in the terminal schema. It is absent from the baseline
    /// create-table statement and was added late in the upgrade chain, by the very same script that
    /// defines the terminal update procedure, as
    /// <c>ALTER TABLE ... Tabs ADD IsSecure bit NOT NULL ... DEFAULT (0)</c>. It is included here
    /// because the terminal procedure writes it; a reader who consulted only the baseline script would
    /// wrongly conclude that this member does not belong.
    /// </para>
    /// <para>
    /// The legacy screen enabled its checkbox only when the portal had SSL configured. That
    /// conditional is a service and configuration concern; this contract carries the submitted value
    /// unconditionally and gates nothing.
    /// </para>
    /// </remarks>
    public bool IsSecure { get; set; }

    // MIGRATION: the recycle-bin flag IS carried on this request, and this is the most consequential
    // decision on the type, so the whole of the reasoning is recorded here.
    //
    // It was NOT a field on the legacy page-settings form. managetabs.ascx declares no such control,
    // and ManageTabs.ascx.vb line 264 hard-coded objTab.IsDeleted = False on every save from that
    // screen. Taken alone that would argue for excluding it.
    //
    // It is included nonetheless, on measured evidence that both recycle-bin transitions travelled
    // through the very same update call this endpoint replaces:
    //   * Soft delete is literally objtab.IsDeleted = True followed by objtabs.UpdateTab(objtab) -
    //     Library/Components/Tabs/TabController.vb lines 836 to 837 - audited as a
    //     page-sent-to-recycle-bin event at line 840.
    //   * Restore is literally objTab.IsDeleted = False followed by the identical
    //     objTabs.UpdateTab(objTab) - Website/admin/Tabs/RecycleBin.ascx.vb lines 278 to 279 -
    //     audited as a page-restored event.
    //   * The flag is one of the terminal update procedure's eighteen mutable parameters.
    //   * The narrow endpoint surface provides no delete endpoint and no restore endpoint, so this
    //     PUT is the ONLY route through which either transition can be expressed.
    // Omitting the member would therefore make soft delete and restore unreachable altogether, which
    // is a loss of the functional parity the migration directive requires.
    //
    // The rejected alternative is recorded so it is not silently revisited: adding a dedicated delete
    // or restore endpoint, with its own request shape, would express the two transitions more
    // explicitly. It is rejected because the plan fixes the page surface at GET and PUT only, and the
    // scope lock forbids any request shape not named for this folder. Should that surface ever be
    // widened, this member is the first thing to reconsider.
    //
    // MIGRATION: one legacy restore constraint is deliberately NOT enforced here. A page could not be
    // restored while its own parent was still deleted - RecycleBin.ascx.vb line 274 refused it with
    // "<b>{0}</b> Page Cannot Be Restored Until Its Parent Is Restored First." Enforcing that requires
    // reading the parent chain, which is page-service work; a hierarchy walk inside a DTO is forbidden
    // outright. The constraint is documented for the service, not implemented here.
    /// <summary>
    /// Gets or sets a value indicating whether the page is soft-deleted into the recycle bin. Maps the
    /// legacy <c>Tabs.IsDeleted</c> column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only member of this request with no counterpart control on the legacy page-settings
    /// form, and it is present for a specific reason: in the legacy application both recycle-bin
    /// transitions were plain writes of this flag through the identical update call that this endpoint
    /// replaces. Sending <see langword="true"/> soft-deletes the page; sending <see langword="false"/>
    /// restores it. Because the page surface exposes no delete route and no restore route, this member
    /// is the sole means of reaching either transition, and dropping it would remove the capability
    /// from the product.
    /// </para>
    /// <para>
    /// A soft-deleted page is still a row, and this contract neither filters nor hides anything - the
    /// service and the endpoint decide which rows a caller may see and which transitions a caller may
    /// perform.
    /// </para>
    /// <para>
    /// Callers must note the consequence of complete-replacement semantics: because no initialiser is
    /// declared, omitting the field binds it to <see langword="false"/>, so a routine edit that omits
    /// it will restore a soft-deleted page. A caller editing a page in the recycle bin must submit
    /// <see langword="true"/> to keep it there. This mirrors the legacy screen, which hard-coded the
    /// flag to false on every save and so had exactly the same effect.
    /// </para>
    /// <para>
    /// Two related constraints are enforced by the page service rather than here. Restoration was
    /// blocked while the page's own parent remained deleted, refused with "Page Cannot Be Restored
    /// Until Its Parent Is Restored First." And deletion was refused outright for the five protected
    /// system pages - the administration, splash, home, login and user pages - and for a page that
    /// still had children. Both require reading other rows, which a DTO must never do.
    /// </para>
    /// <para>
    /// Permanent deletion is a different operation entirely - the legacy recycle bin called a distinct
    /// delete procedure for it - and it is out of the narrow endpoint surface. This member expresses
    /// only the reversible transition.
    /// </para>
    /// </remarks>
    public bool IsDeleted { get; set; }
}
