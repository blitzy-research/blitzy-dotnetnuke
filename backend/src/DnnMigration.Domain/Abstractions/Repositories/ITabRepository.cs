using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// This contract realises the fourteen-member 'tab block of the legacy abstract data provider as one
// aggregate-shaped repository. That class was 397 lines carrying 269 MustOverride members reached through a
// reflection-created static singleton (lines 29-50).

// Legacy UpdateTab existed as a 16-argument overload and a 19-argument overload differing only by the
// RefreshInterval/PageHeadText/IsSecure tail; both wrote the same Tabs row and collapse into a single
// entity-oriented UpdateAsync.

// GetTabPanes is omitted - it projected distinct pane-name strings rather than entities; pane names are
// available via TabModule.PaneName from GetTabModulesAsync.

// GetTabByTabPath and GetTabPathDictionary are omitted - both were computed in memory from the full tab set
// with no backing stored procedure; Tab.TabPath is a mapped column and the derivation belongs in an
// Application service.

// The remaining public TabController members that are not persistence are likewise absent. GetTab(TabId,
// PortalId, ignoreCache) (L467) carried a caching flag, and the twelve DataCache sites in that file become
// the caching service in Infrastructure, so no member here takes a cache flag or promises a cached read.

/// <summary>Reads and writes the <c>dbo.Tabs</c> rows that form a portal's page hierarchy.</summary>
/// <remarks>
/// <para>
/// EVERY MEMBER IS ASYNCHRONOUS AND CANCELLABLE. Each returns a <see cref="Task"/>, carries a trailing
/// cancellation token, and has no synchronous counterpart, so no caller can block a request thread on
/// database work.
/// </para>
/// <para>
/// WRITES ARE STAGED, NEVER COMMITTED. <see cref="AddAsync"/>, <see cref="UpdateAsync"/>, <see
/// cref="UpdateOrderAsync"/> and <see cref="DeleteAsync"/> record an intention; the unit of work commits
/// it.
/// </para>
/// </remarks>
public interface ITabRepository
{
    /// <summary>
    /// Returns the page bearing the supplied identifier, or <see langword="null"/> when no row bears it.
    /// </summary>
    /// <param name="tabId">The page key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching page, or <see langword="null"/> when none matches.</returns>
    Task<Tab?> GetByIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Returns the pages bearing any of the supplied keys, in one read.</summary>
    /// <param name="tabIds">The page keys wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The pages that exist among the supplied keys, ordered by key.</returns>
    Task<IReadOnlyList<Tab>> GetByIdsAsync(
        IReadOnlyCollection<int> tabIds,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every page belonging to one portal, in hierarchy order.</summary>
    /// <remarks>
    /// An implementation must return the rows already ordered so that a parent precedes its own children
    /// and siblings keep their relative positions. <c>Tabs.TabOrder</c> is a single portal-wide sequence
    /// that the legacy ordering routine maintained in steps precisely to make that true, so ordering by it
    /// yields depth-first hierarchy order directly; the legacy procedure's <c>order by TabOrder,
    /// TabName</c> is refined only by appending deterministic tie-breakers, so that rows sharing a position
    /// still have a stable relative order.
    /// </remarks>
    /// <param name="portalId">The portal whose pages are read.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The portal's pages in hierarchy order, recycled pages included; empty when the portal has none.
    /// </returns>
    Task<IReadOnlyList<Tab>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page bearing the supplied identifier ONLY when it belongs to the supplied portal.
    /// </summary>
    /// <remarks>
    /// The tenant-scoped form of <see cref="GetByIdAsync"/>, and it exists because asking whether ONE named
    /// page belongs to a portal is a question the set-based member answered far too expensively.
    /// </remarks>
    /// <param name="portalId">The portal the page must belong to.</param>
    /// <param name="tabId">The page key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The matching page, or <see langword="null"/> when no row bears that key within that portal - whether
    /// because no such page exists at all or because it belongs to another tenant.
    /// </returns>
    Task<Tab?> GetPortalTabAsync(int portalId, int tabId, CancellationToken cancellationToken = default);

    /// <summary>Returns every page in the installation, across all portals and including host-level pages.</summary>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>Every page in the installation; empty when there are none.</returns>
    Task<IReadOnlyList<Tab>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page of one specified portal whose stored name is exactly the supplied value, or <see
    /// langword="null"/> when that portal has no such page.
    /// </summary>
    /// <remarks>
    /// Matching is by equality on the whole stored value: never by prefix, suffix, fragment or pattern. The
    /// legacy procedure returned every match ordered by key and the controller took the first, so where
    /// names are not unique this member yields the lowest-keyed match; page-name uniqueness is a rule the
    /// Application layer applies, because the terminal schema does not enforce it.
    /// </remarks>
    /// <param name="tabName">The page name to match.</param>
    /// <param name="portalId">The portal whose pages are searched.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching page, or <see langword="null"/> when that portal has no page of that name.</returns>
    Task<Tab?> GetByNameAsync(string tabName, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page of one specified portal whose stored name is exactly the supplied value and whose
    /// parent is the supplied page, or <see langword="null"/> when no such page exists.
    /// </summary>
    /// <remarks>
    /// Ports the parent-scoped variant the legacy controller exposed alongside the portal-scoped one, which
    /// narrowed the same name lookup to one branch of the tree so that two sibling groups could each hold a
    /// page of the same name. It is the single additional overload this member takes; the legacy
    /// optional-parameter tails are not reproduced.
    /// </remarks>
    /// <param name="tabName">The page name to match.</param>
    /// <param name="portalId">The portal whose pages are searched.</param>
    /// <param name="parentId">The page that must be the parent of the match.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching child page, or <see langword="null"/> when none matches.</returns>
    Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        int parentId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the pages whose parent is the supplied page, across every portal.</summary>
    /// <param name="parentId">The parent page.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The child pages in order, recycled children included; empty when the page has none.</returns>
    Task<IReadOnlyList<Tab>> GetByParentIdAsync(int parentId, CancellationToken cancellationToken = default);

    /// <summary>Returns the pages of one specified portal whose parent is the supplied page.</summary>
    /// <param name="parentId">The parent page.</param>
    /// <param name="portalId">The portal the children must belong to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The child pages in that portal, in order; empty when there are none.</returns>
    Task<IReadOnlyList<Tab>> GetByParentIdAsync(
        int parentId,
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the portal's page count as the legacy quota metric computed it.</summary>
    /// <param name="portalId">The portal to measure.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The portal's page count on the legacy quota basis.</returns>
    Task<int> CountByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the module placements on one page.</summary>
    /// <remarks>
    /// The placements are returned as entities rather than as the flattened module-and-placement join the
    /// legacy procedure produced, because the legacy single 58-property class that spanned Modules,
    /// TabModules, ModuleDefinitions and ModuleControls is split in the target along the real table
    /// boundaries.
    /// </remarks>
    /// <param name="tabId">The page whose placements are read.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The placements on that page; empty when the page carries no module.</returns>
    Task<IReadOnlyList<TabModule>> GetTabModulesAsync(
        int tabId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the module placements on one page, confirming that the page belongs to the supplied portal.
    /// </summary>
    /// <param name="portalId">The portal the page must belong to.</param>
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

    /// <summary>Returns the identifiers of the pages that have at least one child.</summary>
    /// <param name="portalId">
    /// The portal to restrict the answer to, or <see langword="null"/> to consider every page in the
    /// installation.
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
    /// Reproduces the correlated sub-select that the legacy portal read view resolved into the
    /// <c>SuperTabId</c> column of its result set - <c>select TabId from Tabs where PortalId is null and
    /// ParentId is null</c> - and that every later revision of the view carried unchanged.
    /// </remarks>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// The host-level root page's key, or <see langword="null"/> when the installation has no such page.
    /// </returns>
    Task<int?> GetHostRootTabIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Determines whether a page name is already in use, optionally ignoring one page.</summary>
    /// <remarks>
    /// This is the reporting form of the legacy name lookup: it answers the uniqueness question without
    /// materialising a page, which is what a validator needs. The comparison is portal-wide rather than
    /// sibling-wide, matching the scope of the unique index the legacy schema briefly carried over
    /// <c>(PortalID, TabName)</c> before a later script dropped it again.
    /// </remarks>
    /// <param name="portalId">
    /// The portal whose page names are searched, or <see langword="null"/> to search the host-level pages -
    /// the rows whose portal column is itself null, which is a real stored state and not a stand-in for a
    /// portal numbered 0 or -1.
    /// </param>
    /// <param name="tabName">The page name to test.</param>
    /// <param name="excludingTabId">
    /// A page to disregard, so that renaming a page does not collide with itself, or <see langword="null"/>
    /// to disregard none.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns><see langword="true"/> when another live page in that scope already bears the name.</returns>
    Task<bool> TabNameExistsAsync(
        int? portalId,
        string tabName,
        int? excludingTabId,
        CancellationToken cancellationToken = default);

    /// <summary>Stages the insertion of a new page.</summary>
    /// <remarks>
    /// The page is stored as supplied. Assigning its position in the sequence, its depth, its path and its
    /// parent, and validating any of them, all happen before this member is called.
    /// </remarks>
    /// <param name="tab">The page to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddAsync(Tab tab, CancellationToken cancellationToken = default);

    /// <summary>Stages an update to an existing page.</summary>
    /// <remarks>
    /// This is also how a page is sent to the recycle bin and how it is restored, because the legacy update
    /// procedure's <c>IsDeleted</c> argument was itself just another column write: set <see
    /// cref="Tab.IsDeleted"/> on the page and stage it here. <see cref="DeleteAsync"/> is the different and
    /// permanent operation.
    /// </remarks>
    /// <param name="tab">The page to update, carrying the values to store.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateAsync(Tab tab, CancellationToken cancellationToken = default);

    /// <summary>Stages an update confined to a page's position in the hierarchy.</summary>
    /// <param name="tab">
    /// The page whose position is stored, carrying its sequence number, depth, parent and path.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the positional update has been staged.</returns>
    Task UpdateOrderAsync(Tab tab, CancellationToken cancellationToken = default);

    /// <summary>Stages the permanent removal of a page.</summary>
    /// <remarks>
    /// The page is addressed by key rather than by entity so that a caller need not read a page in order to
    /// remove it. Deciding that removal is permitted - that the page has no children left, that it is not
    /// the portal's home, splash, login, user or administration page, and that the caller may act on it -
    /// happens before this member is called.
    /// </remarks>
    /// <param name="tabId">The page to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteAsync(int tabId, CancellationToken cancellationToken = default);
}
