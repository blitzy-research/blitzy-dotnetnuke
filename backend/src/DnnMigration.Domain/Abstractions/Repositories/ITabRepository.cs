using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: this contract realises the fourteen-member 'tab block of the legacy abstract data provider
// (Library/Components/Providers/Data/DataProvider.vb lines 109-123) as one aggregate-shaped repository.
// That class was 397 lines carrying 269 MustOverride members reached through a reflection-created static
// singleton (lines 29-50). None of the provider metadata constants, the reflective activation, the
// singleton accessor, the raw procedure execution, the reader hydration or the provider-owned transaction
// is translated: the surface is decomposed by aggregate and resolved by dependency injection instead.
// Thirteen of the fourteen members appear below; the fourteenth is deliberately omitted for the reason
// recorded further down.

// MIGRATION: legacy AddTab (DataProvider.vb:L110) took 18 positional arguments; the target passes the Tab
// entity.

// MIGRATION: legacy UpdateTab existed as a 16-argument overload (DataProvider.vb:L111) and a 19-argument
// overload (L112) differing only by the RefreshInterval/PageHeadText/IsSecure tail; both wrote the same
// Tabs row and collapse into a single entity-oriented UpdateAsync.

// MIGRATION: GetTabPanes (DataProvider.vb:L123) is omitted - it projected distinct pane-name strings
// rather than entities; pane names are available via TabModule.PaneName from GetTabModulesAsync. The
// terminal definition is literally "select distinct(PaneName) as PaneName from TabModules where TabId =
// @TabId order by PaneName" (03.00.01.SqlDataProvider lines 513-523), so it reads the very rows
// GetTabModulesAsync already returns and adds no fact this contract cannot supply.

// MIGRATION: GetTabByTabPath (TabController.vb:L1101) and GetTabPathDictionary (L1111) are omitted - both
// were computed in memory from the full tab set with no backing stored procedure; Tab.TabPath is a mapped
// column and the derivation belongs in an Application service.

// MIGRATION: TabController.UpdatePortalTabOrder (L550, seven parameters with an Optional NewTab tail) and
// the private MoveTab helper (L243) are multi-step in-memory reordering orchestration and are not
// persistence operations; the persistence primitive is UpdateTabOrder (DataProvider.vb:L113), surfaced
// here as UpdateOrderAsync. Reordering orchestration belongs to the Application TabService.

// MIGRATION: the remaining public TabController members that are not persistence are likewise absent.
// GetTab(TabId, PortalId, ignoreCache) (L467) carried a caching flag, and the twelve DataCache sites in
// that file become the caching service in Infrastructure, so no member here takes a cache flag or promises
// a cached read. CopyDesignToChildren (L375), CopyPermissionsToChildren (L387) and CopyTab (L414) are
// multi-step orchestration across tabs, modules and permissions. DeleteTab(tabId, PortalSettings, UserId)
// (L936) took the ambient per-request composite that becomes the scoped portal context, so no member here
// accepts a context object. DeserializePanes (L984), the DeserializeTab overloads (L1009-L1025) and the
// SerializeTab overloads (L1141-L1166) exchanged XmlNode, XmlDocument and Hashtable, and portal-template
// import is Application-layer work. GetTabsByPortal (L528) returned Dictionary(Of Integer, TabInfo); the
// keyed shape is not reproduced, only the concept, as an IReadOnlyList<Tab>.

// MIGRATION: TabInfo.vb (616 lines, 36 properties) declared Implements IPropertyAccess for the
// token-replacement subsystem, and carried XML serialisation concerns. Both are dropped: the Domain entity
// is a plain object and the wire contract belongs to the Application DTOs.

/// <summary>
/// Reads and writes the <c>dbo.Tabs</c> rows that form a portal's page hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// This is the persistence contract for the page aggregate and nothing more. Tab is a supporting
/// aggregate rather than a target domain in its own right: it is drawn in because module placement, tab
/// permissions and portal navigation are inseparable from it, and the permission tables are keyed by
/// <c>TabID</c>. Everything here is a read or a staged write. Choosing where a page sits in the tree,
/// renumbering its siblings, copying a page's design or permissions to its children, moving a page
/// between parents, emptying the recycle bin and importing a portal template all belong to the
/// Application layer.
/// </para>
/// <para>
/// EVERY MEMBER IS ASYNCHRONOUS AND CANCELLABLE. Each returns a <see cref="Task"/>, carries a trailing
/// cancellation token, and has no synchronous counterpart, so no caller can block a request thread on
/// database work.
/// </para>
/// <para>
/// WRITES ARE STAGED, NEVER COMMITTED. <see cref="AddAsync"/>, <see cref="UpdateAsync"/>,
/// <see cref="UpdateOrderAsync"/> and <see cref="DeleteAsync"/> record an intention; the unit of work
/// commits it. That is what allows a portal's first pages to be written in the same transaction as the
/// portal, its alias, its roles and its modules - the five tables the legacy portal creation touched in
/// sequence without a transaction spanning them - and it is why no write member returns a generated key.
/// </para>
/// <para>
/// NEITHER -1 NOR 0 MEANS "ABSENT" IN ANY MEMBER OF THIS CONTRACT. <c>dbo.Tabs.TabID</c> is
/// <c>IDENTITY(0, 1)</c>, so 0 is a real page key; <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
/// and the shipped default portal is 0, so both -1 and 0 are real portal keys. The legacy null-integer
/// sentinel was -1 and the legacy null test could not tell it from a genuine -1, which is why shipped code
/// could pass that sentinel as a portal identifier and mean it. Here an identifier parameter always
/// denotes exactly the row bearing it. Absence is expressed by a nullable return, never by a numeric
/// value. In particular a page with no parent is one whose <see cref="Tab.ParentId"/> is
/// <see langword="null"/>: <c>ParentId = 0</c> is the page whose key is 0, not the absence of a parent.
/// Restoring sentinel values for an external contract is a concern of the DTO and API boundary and never
/// reaches these signatures.
/// </para>
/// <para>
/// DELETION HAS TWO DISTINCT MEANINGS AND THIS CONTRACT KEEPS THEM APART, because the legacy schema did.
/// Sending a page to the recycle bin is a field change - set <see cref="Tab.IsDeleted"/> and stage
/// <see cref="UpdateAsync"/> - and is exactly what the legacy update procedure's <c>IsDeleted</c> argument
/// did. Removing a page permanently is <see cref="DeleteAsync"/>, whose legacy counterpart
/// <c>DeleteTab</c> was a literal <c>delete from Tabs where TabId = @TabId</c>
/// (02.00.00.SqlDataProvider lines 1446-1455). Neither is a substitute for the other, and no read member
/// silently hides recycled pages: <see cref="Tab.IsDeleted"/> is a mapped column that reads return, so
/// whether a listing shows recycled pages is the caller's policy and not this contract's.
/// </para>
/// <para>
/// PAGE NAMES ARE MATCHED EXACTLY AND NEVER AS PATTERNS. No member accepts a fragment, a pattern or a
/// search term, so a supplied value is data throughout and no character within it can broaden what
/// matches.
/// </para>
/// </remarks>
public interface ITabRepository
{
    /// <summary>
    /// Returns the page bearing the supplied identifier, or <see langword="null"/> when no row bears it.
    /// </summary>
    /// <remarks>
    /// Ports <c>GetTab</c> (DataProvider.vb:L117), which returned a reader over nought or one row that the
    /// legacy controller hydrated by hand. The absent case is a legitimate outcome reported as
    /// <see langword="null"/> rather than as an exception, because a caller acting on a client-supplied
    /// identifier cannot know in advance that the row exists. A recycled page is still a page and is
    /// returned here; callers that must exclude one test <see cref="Tab.IsDeleted"/> themselves.
    /// </remarks>
    /// <param name="tabId">
    /// The page key. Every value denotes exactly the row bearing it. This identity seeds at 0, so 0 is a
    /// real page and is never read as a request for "any" or "no" row.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching page, or <see langword="null"/> when none matches.</returns>
    Task<Tab?> GetByIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every page belonging to one portal, in hierarchy order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabs</c> (DataProvider.vb:L115). Its terminal definition filtered on the portal alone -
    /// <c>where Tabs.PortalId = @PortalId</c>, 03.01.01.SqlDataProvider lines 487-490 - and applied no
    /// predicate to <c>IsDeleted</c>, projecting that column instead so the caller could decide. This
    /// member preserves exactly that: recycled pages are included, because they still occupy positions in
    /// the ordering and a caller renumbering the tree must see them. A listing that must hide them filters
    /// on <see cref="Tab.IsDeleted"/> itself, which is where that policy belongs.
    /// </para>
    /// <para>
    /// An implementation must return the rows already ordered so that a parent precedes its own children
    /// and siblings keep their relative positions. <c>Tabs.TabOrder</c> is a single portal-wide sequence
    /// that the legacy ordering routine maintained in steps precisely to make that true, so ordering by it
    /// yields depth-first hierarchy order directly; the legacy procedure's <c>order by TabOrder, TabName</c>
    /// is refined only by appending deterministic tie-breakers, so that rows sharing a position still have
    /// a stable relative order. Callers rely on this and are documented not to re-sort.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The portal whose pages are read. Both -1 and 0 are genuine portal keys in this schema, so each
    /// denotes that portal and neither widens the query. Host-level pages carry no portal at all and are
    /// therefore not reachable through this member.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The portal's pages in hierarchy order, recycled pages included; empty when the portal has none.
    /// </returns>
    Task<IReadOnlyList<Tab>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every page in the installation, across all portals and including host-level pages.
    /// </summary>
    /// <remarks>
    /// Ports <c>GetAllTabs</c> (DataProvider.vb:L116, terminal definition at 03.01.01.SqlDataProvider
    /// line 597), which selected the same columns as the portal-scoped read with no portal predicate at
    /// all. It is deliberately a separate member rather than a sentinel understood by
    /// <see cref="GetByPortalIdAsync"/>: -1 and 0 are both real portal keys here, so no numeric value is
    /// free to mean "every portal". Being unbounded, this is an administrative and host-level read, not the
    /// member a portal screen should use.
    /// </remarks>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>Every page in the installation; empty when there are none.</returns>
    Task<IReadOnlyList<Tab>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page of one specified portal whose stored name is exactly the supplied value, or
    /// <see langword="null"/> when that portal has no such page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabByName</c> (DataProvider.vb:L118). Both legacy parameters are preserved because both
    /// were load-bearing: the terminal procedure filtered on the name AND the portal - <c>where TabName =
    /// @TabName and ((Tabs.PortalId = @PortalId) or (@PortalId is null and Tabs.PortalId is null))</c>,
    /// 03.01.01.SqlDataProvider lines 534-535 - which makes this a portal-scoped question and not a
    /// resolution of a name to a page anywhere in the installation.
    /// </para>
    /// <para>
    /// MIGRATION: that predicate also had a second branch, matching host-level pages when the portal
    /// argument was null. It is not surfaced, because the legacy provider member itself declared the
    /// parameter as a non-nullable integer and so could not reach that branch; the host-level pages are
    /// read through <see cref="GetAllAsync"/> and the host-level uniqueness question is answered by
    /// <see cref="TabNameExistsAsync"/>, which does accept a null portal. The narrowing is deliberate and
    /// recorded rather than silently absorbed.
    /// </para>
    /// <para>
    /// Matching is by equality on the whole stored value: never by prefix, suffix, fragment or pattern. The
    /// legacy procedure returned every match ordered by key and the controller took the first, so where
    /// names are not unique this member yields the lowest-keyed match; page-name uniqueness is a rule the
    /// Application layer applies, because the terminal schema does not enforce it.
    /// </para>
    /// </remarks>
    /// <param name="tabName">The page name to match. Untrusted data, and never treated as a pattern.</param>
    /// <param name="portalId">
    /// The portal whose pages are searched. Both -1 and 0 are genuine portal keys, so each denotes that
    /// portal and neither widens the query.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching page, or <see langword="null"/> when that portal has no page of that name.</returns>
    Task<Tab?> GetByNameAsync(string tabName, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page of one specified portal whose stored name is exactly the supplied value and whose
    /// parent is the supplied page, or <see langword="null"/> when no such page exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports the parent-scoped variant the legacy controller exposed alongside the portal-scoped one
    /// (TabController.vb:L508), which narrowed the same name lookup to one branch of the tree so that two
    /// sibling groups could each hold a page of the same name. It is the single additional overload this
    /// member takes; the legacy optional-parameter tails are not reproduced.
    /// </para>
    /// <para>
    /// It is expressed as an overload rather than as a nullable parameter on
    /// <see cref="GetByNameAsync(string, int, CancellationToken)"/> deliberately. A null parent would be
    /// ambiguous between "in any branch" and the real stored meaning of <c>ParentId IS NULL</c>, which is
    /// "a page at the root of the tree" - and encoding two meanings in one value is the sentinel confusion
    /// this migration removes rather than carries forward. Root-level pages are therefore found through the
    /// portal-scoped overload combined with a <see cref="Tab.ParentId"/> test, or through
    /// <see cref="GetByParentIdAsync(int, int, CancellationToken)"/> for a known parent.
    /// </para>
    /// </remarks>
    /// <param name="tabName">The page name to match. Untrusted data, and never treated as a pattern.</param>
    /// <param name="portalId">
    /// The portal whose pages are searched. Both -1 and 0 are genuine portal keys and neither widens the
    /// query.
    /// </param>
    /// <param name="parentId">
    /// The page that must be the parent of the match. This identity seeds at 0, so 0 denotes the page whose
    /// key is 0 and never "no parent".
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching child page, or <see langword="null"/> when none matches.</returns>
    Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        int parentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the pages whose parent is the supplied page, across every portal.
    /// </summary>
    /// <remarks>
    /// Ports <c>GetTabsByParentId</c> (DataProvider.vb:L119), whose terminal definition filtered on the
    /// parent alone and ordered by the portal-wide sequence - <c>where Tabs.ParentId = @ParentId order by
    /// TabOrder</c>, 03.01.01.SqlDataProvider lines 448-449 - with no portal predicate and no
    /// <c>IsDeleted</c> predicate. Both omissions are preserved: the result is not restricted to one portal
    /// and recycled children are included. Because a page's parent uniquely determines its portal in
    /// practice, the unscoped form is safe for a caller that already trusts the parent; a caller that does
    /// not should use the portal-scoped overload.
    /// </remarks>
    /// <param name="parentId">
    /// The parent page. This identity seeds at 0, so 0 denotes the page whose key is 0. Pages at the root of
    /// the tree store no parent at all and are consequently not addressable through this member; they are
    /// read from <see cref="GetByPortalIdAsync"/> and identified by a <see langword="null"/>
    /// <see cref="Tab.ParentId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The child pages in order, recycled children included; empty when the page has none.</returns>
    Task<IReadOnlyList<Tab>> GetByParentIdAsync(int parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the pages of one specified portal whose parent is the supplied page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports the portal-scoped variant the legacy controller exposed alongside the unscoped one
    /// (TabController.vb:L524), which applied the portal filter after the fact. It is the single additional
    /// overload this member takes.
    /// </para>
    /// <para>
    /// It is an overload rather than a nullable portal parameter for the same reason given on
    /// <see cref="GetByNameAsync(string, int, int, CancellationToken)"/>: a null portal would be ambiguous
    /// between "every portal" and the real stored meaning of <c>PortalId IS NULL</c>, which is "a
    /// host-level page". <see cref="GetByParentIdAsync(int, CancellationToken)"/> is the explicit
    /// every-portal form, so neither meaning has to be inferred from a value.
    /// </para>
    /// </remarks>
    /// <param name="parentId">
    /// The parent page. This identity seeds at 0, so 0 denotes the page whose key is 0 and never "no
    /// parent".
    /// </param>
    /// <param name="portalId">
    /// The portal the children must belong to. Both -1 and 0 are genuine portal keys and neither widens the
    /// query.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The child pages in that portal, in order; empty when there are none.</returns>
    Task<IReadOnlyList<Tab>> GetByParentIdAsync(
        int parentId,
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal's page count as the legacy quota metric computed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabCount</c> (DataProvider.vb:L120). THIS IS NOT A PLAIN ROW COUNT, and an
    /// implementation that returns one is wrong. The terminal procedure resolves the portal's
    /// administration page and then counts the portal's pages excluding it, subtracting one further:
    /// <c>set @AdminTabId = (select AdminTabId from Portals where PortalID = @PortalID)</c> then
    /// <c>select count(*) - 1 from Tabs where (PortalID = @PortalID) and (TabID &lt;&gt; @AdminTabId)</c>
    /// (04.04.00.SqlDataProvider lines 511-525). Both adjustments are load-bearing, because this figure is
    /// the portal's advertised page total - it populated the <c>Pages</c> property read at
    /// PortalInfo.vb:L324 through TabController.vb lines 512-513 - and that total is what the portal's page
    /// quota is compared against. Reproducing the arithmetic preserves measured behaviour; "correcting" it
    /// would silently change every quota decision.
    /// </para>
    /// <para>
    /// Recycled pages were counted by the legacy procedure, which applied no <c>IsDeleted</c> predicate, and
    /// they are counted here. The count is derived from stored rows only; it takes no filter, no search term
    /// and no paging argument, because the legacy tab surface had none.
    /// </para>
    /// <para>
    /// This procedure is also the clearest illustration of why the DDL chain must be searched
    /// case-insensitively and in every naming form: it exists only in the bracketed, templated form
    /// <c>{databaseOwner}[{objectQualifier}GetTabCount]</c>, and a search for the unbracketed spelling
    /// reports it as absent although the provider calls it.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The portal to measure. Both -1 and 0 are genuine portal keys and neither widens the query.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The portal's page count on the legacy quota basis. A portal holding only its administration page
    /// yields a negative figure, exactly as the legacy expression did; the value is a metric to be reported
    /// and compared, never an identifier and never a presence test.
    /// </returns>
    Task<int> CountByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the module placements on one page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabModules</c> (DataProvider.vb:L122) - "which modules are placed on this page". This
    /// member and <see cref="GetPortalTabModulesAsync"/> are the ONLY <see cref="TabModule"/> members on
    /// this contract, and both are reads. The ownership split is deliberate: adding, updating and deleting a
    /// placement, and everything to do with module settings and per-placement settings, belong to the module
    /// repository, which owns the module aggregate that a placement is part of. A page owns where modules
    /// sit on it; it does not own their lifecycle.
    /// </para>
    /// <para>
    /// The placements are returned as entities rather than as the flattened module-and-placement join the
    /// legacy procedure produced, because the legacy single 58-property class that spanned Modules,
    /// TabModules, ModuleDefinitions and ModuleControls is split in the target along the real table
    /// boundaries.
    /// </para>
    /// </remarks>
    /// <param name="tabId">
    /// The page whose placements are read. This identity seeds at 0, so 0 is a real page.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The placements on that page; empty when the page carries no module.</returns>
    Task<IReadOnlyList<TabModule>> GetTabModulesAsync(
        int tabId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the module placements on one page, confirming that the page belongs to the supplied portal.
    /// </summary>
    /// <remarks>
    /// Ports <c>GetPortalTabModules</c> (DataProvider.vb:L121, terminal definition at
    /// 03.00.08.SqlDataProvider line 313), which took the portal and the page together. The portal argument
    /// is preserved because it is a tenant-isolation guard rather than a redundant filter: a page identifier
    /// arriving from a client must not be allowed to read another tenant's page content merely because the
    /// identifier is well formed. An implementation must therefore return nothing at all when the page does
    /// not belong to that portal, rather than ignoring the portal.
    /// </remarks>
    /// <param name="portalId">
    /// The portal the page must belong to. Both -1 and 0 are genuine portal keys and neither widens the
    /// query.
    /// </param>
    /// <param name="tabId">The page whose placements are read. 0 is a real page.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The placements on that page; empty when the page carries no module OR when the page does not belong
    /// to that portal.
    /// </returns>
    Task<IReadOnlyList<TabModule>> GetPortalTabModulesAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the identifiers of the pages that have at least one child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this replaces a correlated sub-select the legacy page reads carried in their own projection
    /// - <c>'HasChildren' = case when exists (select 1 from Tabs T2 where T2.ParentId = Tabs.TabId) then
    /// 'true' else 'false' end</c>, present in the terminal <c>GetTabs</c> at 03.01.01.SqlDataProvider
    /// line 484 and in its sibling reads. Evaluating it per row is what this member exists to avoid: one
    /// round trip answers the question for a whole listing, and the fact is returned as a set of keys rather
    /// than as a flag smeared across the rows. No page is fetched to answer it.
    /// </para>
    /// <para>
    /// Recycled pages are not treated as children, because a parent whose only children are in the recycle
    /// bin has no child a caller can navigate to - which is the question the legacy flag was asked to
    /// answer.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The portal to restrict the answer to, or <see langword="null"/> to consider every page in the
    /// installation. Here <see langword="null"/> is an explicit "not restricted" and is available only
    /// because this member returns a derived projection rather than addressing rows by key; a portal
    /// identifier still denotes exactly that portal, and -1 and 0 remain genuine portal keys.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The distinct keys of the pages that have at least one live child, as a set so that a caller's
    /// per-row membership test is a hash lookup; empty when no page has children.
    /// </returns>
    Task<IReadOnlyCollection<int>> ListParentTabIdsAsync(
        int? portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the identifier of the host-level root page, or <see langword="null"/> when none exists.
    /// </summary>
    /// <remarks>
    /// MIGRATION: reproduces the correlated sub-select that the legacy portal read view resolved into the
    /// <c>SuperTabId</c> column of its result set - <c>select TabId from Tabs where PortalId is null and
    /// ParentId is null</c> - and that every later revision of the view carried unchanged. The value is
    /// identical for every portal because it is not stored against a portal, which is why it is read once
    /// here rather than projected onto every portal row. Host-level pages are the rows whose portal column
    /// is itself null, so this is a genuine absence of a portal and not a portal numbered 0 or -1. Because
    /// <c>TabID</c> is <c>IDENTITY(0, 1)</c>, an absent host root must be reported as
    /// <see langword="null"/> and never as 0 or -1.
    /// </remarks>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The host-level root page's key, or <see langword="null"/> when the installation has no such page.
    /// </returns>
    Task<int?> GetHostRootTabIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a page name is already in use, optionally ignoring one page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reporting form of the legacy name lookup: it answers the uniqueness question without
    /// materialising a page, which is what a validator needs. The comparison is portal-wide rather than
    /// sibling-wide, matching the scope of the unique index the legacy schema briefly carried over
    /// <c>(PortalID, TabName)</c> before a later script dropped it again. Because that index is absent from
    /// the terminal schema, uniqueness is a rule the Application layer applies rather than one the store
    /// enforces - which is precisely why this member reports a fact instead of throwing.
    /// </para>
    /// <para>
    /// An implementation must compare names without regard to case, and must exclude recycled pages so that
    /// sending a page to the recycle bin genuinely frees its name for reuse.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The portal whose page names are searched, or <see langword="null"/> to search the host-level pages -
    /// the rows whose portal column is itself null, which is a real stored state and not a stand-in for a
    /// portal numbered 0 or -1. This member accepts that null precisely because the legacy name predicate
    /// had a host-level branch.
    /// </param>
    /// <param name="tabName">The page name to test. Untrusted data, and never treated as a pattern.</param>
    /// <param name="excludingTabId">
    /// A page to disregard, so that renaming a page does not collide with itself, or
    /// <see langword="null"/> to disregard none. A supplied 0 excludes the page whose key is 0, since that
    /// is a real page.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// <see langword="true"/> when another live page in that scope already bears the name.
    /// </returns>
    Task<bool> TabNameExistsAsync(
        int? portalId,
        string tabName,
        int? excludingTabId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the insertion of a new page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>AddTab</c> (DataProvider.vb:L110), whose 18 positional arguments are replaced by the entity.
    /// </para>
    /// <para>
    /// THIS MEMBER RETURNS <see cref="Task"/> AND NOT <c>Task&lt;int&gt;</c>, although the legacy member
    /// returned the generated key because its procedure ended in <c>SCOPE_IDENTITY()</c>. The generated key
    /// is not available until the unit of work commits, so a member that promised to return it would have to
    /// commit on the caller's behalf - which would dissolve the transaction boundary and make it impossible
    /// to write a portal's first pages in the same transaction as the portal, its alias, its roles and its
    /// modules. The page's key is therefore read from <see cref="Tab.TabId"/> after
    /// <c>SaveChangesAsync</c> has run on the unit of work, not from this member's result.
    /// </para>
    /// <para>
    /// The page is stored as supplied. Assigning its position in the sequence, its depth, its path and its
    /// parent, and validating any of them, all happen before this member is called.
    /// </para>
    /// </remarks>
    /// <param name="tab">The page to insert. Its key is assigned by the store, not by the caller.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddAsync(Tab tab, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an update to an existing page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports both legacy <c>UpdateTab</c> overloads - the 16-argument form at DataProvider.vb:L111 and the
    /// 19-argument form at L112 - which differed only by a <c>RefreshInterval</c>, <c>PageHeadText</c> and
    /// <c>IsSecure</c> tail and wrote the same row. One entity-oriented member replaces both, so the
    /// distinction disappears rather than being carried forward as an overload pair.
    /// </para>
    /// <para>
    /// This is also how a page is sent to the recycle bin and how it is restored, because the legacy update
    /// procedure's <c>IsDeleted</c> argument was itself just another column write: set
    /// <see cref="Tab.IsDeleted"/> on the page and stage it here. <see cref="DeleteAsync"/> is the different
    /// and permanent operation.
    /// </para>
    /// </remarks>
    /// <param name="tab">The page to update, carrying the values to store.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateAsync(Tab tab, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an update confined to a page's position in the hierarchy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>UpdateTabOrder</c> (DataProvider.vb:L113). Its five positional arguments are all properties
    /// of the page, and the legacy controller already passed the whole page object to its own wrapper, so the
    /// entity is passed here too. The terminal procedure wrote exactly four columns - <c>set TabOrder =
    /// @TabOrder, [Level] = @Level, ParentId = @ParentId, TabPath = @TabPath where TabId = @TabId</c>,
    /// 04.05.00.SqlDataProvider lines 1815-1828 - and that narrowness is the whole point of keeping this
    /// member separate from <see cref="UpdateAsync"/>: renumbering a tree touches many pages, and it must not
    /// carry each one's unrelated edits along with it.
    /// </para>
    /// <para>
    /// MIGRATION: this is the persistence primitive that the legacy reordering path eventually reached. The
    /// path itself is not surfaced: <c>UpdatePortalTabOrder</c> (TabController.vb:L550) took seven parameters
    /// with an optional tail and drove the private <c>MoveTab</c> helper (L243) across an in-memory list of
    /// the portal's pages before persisting anything, so it is multi-step orchestration rather than a
    /// persistence operation. Deciding which pages move, to what depth and in what order is the Application
    /// layer's work; this member only records the outcome for one page.
    /// </para>
    /// </remarks>
    /// <param name="tab">
    /// The page whose position is stored, carrying its sequence number, depth, parent and path.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the positional update has been staged.</returns>
    Task UpdateOrderAsync(Tab tab, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the permanent removal of a page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>DeleteTab</c> (DataProvider.vb:L114), which was a literal <c>delete from Tabs where TabId =
    /// @TabId</c> (02.00.00.SqlDataProvider lines 1446-1455). It is therefore a hard delete and is NOT the
    /// recycle bin: sending a page to the recycle bin is a change to <see cref="Tab.IsDeleted"/> staged
    /// through <see cref="UpdateAsync"/>. This member is what permanently emptying the recycle bin reaches.
    /// </para>
    /// <para>
    /// The page is addressed by key rather than by entity so that a caller need not read a page in order to
    /// remove it. Deciding that removal is permitted - that the page has no children left, that it is not the
    /// portal's home, splash, login, user or administration page, and that the caller may act on it - happens
    /// before this member is called. Removing what depended on the page is likewise the caller's affair:
    /// legacy deletion of a page ran alongside separate cleanup of its module placements and permissions, and
    /// this member neither performs nor implies that cleanup.
    /// </para>
    /// </remarks>
    /// <param name="tabId">
    /// The page to remove. This identity seeds at 0, so 0 identifies a real page; no value is read as "no
    /// page" and passing one that does not exist removes nothing.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteAsync(int tabId, CancellationToken cancellationToken = default);
}
