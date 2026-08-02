using System.Text.RegularExpressions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Services;

/// <summary>
/// Reads and updates the page hierarchy of a portal.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: absorbs the read and update halves of the legacy
/// <c>Library/Components/Tabs/TabController.vb</c> (1,302 lines). Six legacy read members collapse
/// into <see cref="GetTabsAsync"/>, four more into <see cref="GetTabAsync"/>, and four update members
/// - <c>UpdateTab</c> (L780), <c>UpdateTabOrder</c> (L816), the five-argument <c>UpdateTabOrder</c>
/// (L1287) and the seven-argument <c>UpdatePortalTabOrder</c> (L550) - collapse into
/// <see cref="UpdateTabAsync"/>. Creation, deletion, copying, restoration, template serialisation and
/// permission propagation are all outside this service's contract.
/// </para>
/// <para>
/// MIGRATION: the twelve legacy cache call sites measured in the page controller are absorbed here.
/// The legacy key shape <c>Tabs{portalId}</c> and the twenty-minute base timeout multiplied by the
/// installation-wide performance setting are both preserved, so cache behaviour stays auditable
/// against the original. The legacy convention that a resolved timeout of zero disables caching
/// altogether is preserved as well; unlike the legacy code, a disabled cache never skips the database
/// read, because returning a stale or empty answer to avoid a query is not defensible.
/// </para>
/// <para>
/// MIGRATION: page ordering. The legacy reordering routine spliced a flat, hierarchy-ordered list and
/// then renumbered every row in steps of two, seeding ordinary pages from -1 and pages in the
/// administration band from 9,999 so that administration pages always sorted last. That end state is
/// reproduced exactly by a depth-first walk of the reconstructed tree, which is both simpler and
/// deterministic. Two further legacy conventions are preserved deliberately: a stored order of zero
/// was treated as "unset" and sorted last, and the administration band was classified by testing a
/// page against the portal's administration page and its immediate children only - so a deeper
/// descendant of the administration page was renumbered in the ordinary band. Both are faithful to
/// the original rather than tidied.
/// </para>
/// <para>
/// MIGRATION: the materialised hierarchy path. The legacy path generator lived in the excluded
/// <c>Globals</c> module (L2385) and delegated its character filtering to the excluded HTML utility
/// (<c>StripNonWord</c>, L327). Neither is reachable, so the two small behaviours actually needed -
/// prepending each ancestor's stripped name and stripping every non-word character - are reproduced
/// here. The legacy filter was expressed as <c>\W*</c> with an empty replacement, which produces
/// output identical to removing every single non-word character, so the simpler form is used.
/// </para>
/// <para>
/// This service reaches persistence only through repository abstractions, never through a database
/// context, a query root or SQL text, and commits every multi-row write exactly once through the unit
/// of work.
/// </para>
/// </remarks>
public sealed class TabService : ITabService
{
    /// <summary>Reason code reported when the named portal does not exist.</summary>
    private const string PortalNotFoundCode = "tab.portal_not_found";

    /// <summary>Reason code reported when the page being updated does not exist.</summary>
    private const string NotFoundCode = "tab.not_found";

    /// <summary>Reason code reported when the requested parent page does not exist.</summary>
    private const string ParentNotFoundCode = "tab.parent_not_found";

    /// <summary>Reason code reported when the requested parent belongs to another portal.</summary>
    private const string ParentCrossPortalCode = "tab.parent_cross_portal";

    /// <summary>Reason code reported when the requested parent is the page itself or a descendant.</summary>
    private const string ParentCycleCode = "tab.parent_cycle";

    /// <summary>Reason code reported when the page name is a reserved device name.</summary>
    private const string NameReservedCode = "tab.name_reserved";

    /// <summary>
    /// Legacy cache key shape for a portal's page collection, preserved verbatim from
    /// <c>DataCache.TabCacheKey</c> (L50).
    /// </summary>
    private const string TabCacheKeyFormat = "Tabs{0}";

    /// <summary>
    /// Legacy base cache timeout in minutes for a portal's page collection, preserved verbatim from
    /// <c>DataCache.TabCacheTimeOut</c> (L51). The effective timeout is this value multiplied by the
    /// installation-wide performance setting.
    /// </summary>
    private const int TabCacheTimeOutMinutes = 20;

    /// <summary>
    /// Separator that opens every segment of the materialised hierarchy path, preserved from the
    /// legacy path generator.
    /// </summary>
    private const string TabPathSeparator = "//";

    /// <summary>
    /// Stored order value the legacy reordering routine treated as "unset", sorting such a page after
    /// its explicitly ordered siblings.
    /// </summary>
    private const int UnsetTabOrder = 0;

    /// <summary>
    /// Order the legacy reordering routine substituted for <see cref="UnsetTabOrder"/> while sorting.
    /// </summary>
    private const int UnsetTabOrderSortValue = 999;

    /// <summary>Seed of the ordinary page order counter, preserved from the legacy routine.</summary>
    private const int DesktopTabOrderSeed = -1;

    /// <summary>
    /// Seed of the administration page order counter, preserved from the legacy routine, which chose
    /// it so that administration pages always sort after the five thousand ordinary pages a portal
    /// could hold.
    /// </summary>
    private const int AdminTabOrderSeed = 9999;

    /// <summary>Step by which the legacy routine advanced either order counter.</summary>
    private const int TabOrderStep = 2;

    /// <summary>Depth assigned to a page that has no parent.</summary>
    private const int RootLevel = 0;

    /// <summary>
    /// Device names the legacy page-management screen refused, reproduced from
    /// <c>Website/admin/Tabs/ManageTabs.ascx.vb</c> line 272. The legacy pattern listed
    /// <c>^CON$</c> twice, which is redundant and is therefore stated once here.
    /// </summary>
    private static readonly Regex ReservedNamePattern = new(
        "^AUX$|^CON$|^NUL$|^COM[1-9]$|^LPT[1-9]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Removes every character the legacy path generator excluded from a path segment.</summary>
    private static readonly Regex NonWordPattern = new(@"\W", RegexOptions.CultureInvariant);

    private readonly ITabRepository _tabs;
    private readonly IPortalRepository _portals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly CachingOptions _caching;

    /// <summary>
    /// Initialises a new instance of the <see cref="TabService"/> class.
    /// </summary>
    /// <param name="tabs">Page repository.</param>
    /// <param name="portals">Portal repository, consulted to prove tenancy before a listing.</param>
    /// <param name="unitOfWork">Commits the page tree in a single transaction.</param>
    /// <param name="cache">Absorbs the legacy page-collection cache.</param>
    /// <param name="caching">
    /// Bound caching configuration. This is a plain settings object rather than a wrapped options
    /// accessor: the application layer deliberately takes no dependency on the options package, and
    /// the API layer registers the resolved value as a singleton.
    /// </param>
    public TabService(
        ITabRepository tabs,
        IPortalRepository portals,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        CachingOptions caching)
    {
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<TabListItemDto>>> GetTabsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        bool portalExists = await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false);
        if (!portalExists)
        {
            return Result<IReadOnlyList<TabListItemDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        string cacheKey = string.Format(System.Globalization.CultureInfo.InvariantCulture, TabCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(TabCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: the legacy reader skipped the database entirely when the resolved timeout was
        // zero, because it judged the load too costly to repeat per request. That shortcut is not
        // reproduced - the read always runs - but the "caching disabled" branch itself is, so a zero
        // multiplier genuinely bypasses the store instead of writing an entry that expires at once.
        IReadOnlyList<TabListItemDto> rows = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadPortalTabsAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadPortalTabsAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<TabListItemDto>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result<TabDetailDto?>> GetTabAsync(int tabId, CancellationToken cancellationToken = default)
    {
        Tab? tab = await _tabs.GetAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null)
        {
            // Absence is an ordinary outcome of a lookup, so it is reported as a success carrying no
            // value rather than as a fabricated failure. The API layer still renders it as 404.
            return Result<TabDetailDto?>.Success(null);
        }

        bool hasChildren = await HasChildrenAsync(tab, cancellationToken).ConfigureAwait(false);
        return Result<TabDetailDto?>.Success(TabMappings.ToDetail(tab, hasChildren));
    }

    /// <inheritdoc />
    public async Task<Result<TabDetailDto>> UpdateTabAsync(
        int tabId,
        UpdateTabRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Tab? tab = await _tabs.GetAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null)
        {
            // Unlike the read member, a mutation names a resource it expects to act upon, so a
            // missing target is an expected failure rather than an empty success.
            return Result<TabDetailDto>.Failure(NotFoundCode, $"No page bears identifier {tabId}.");
        }

        if (ReservedNamePattern.IsMatch(request.TabName))
        {
            return Result<TabDetailDto>.Failure(
                NameReservedCode,
                "The page name is a reserved device name and cannot be used.");
        }

        // The whole portal is needed for the ancestry test and for the renumbering pass that follows,
        // so it is read once here and reused. Pages in the recycle bin are included because they
        // still occupy positions in the legacy ordering, exactly as the legacy list did.
        IReadOnlyList<Tab> siblingSet = tab.PortalId is int owningPortalId
            ? await _tabs.ListAsync(owningPortalId, includeDeleted: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<Tab>();

        Result<TabDetailDto>? parentRejection =
            await ValidateParentAsync(tab, request.ParentId, siblingSet, cancellationToken).ConfigureAwait(false);
        if (parentRejection is not null)
        {
            return parentRejection;
        }

        TabMappings.ApplyUpdate(tab, request);

        if (tab.PortalId is int portalId)
        {
            // The tracked aggregate and the listed instance may be distinct objects, so the listed
            // set is rebuilt with the tracked page substituted in before the tree is recomputed.
            List<Tab> tree = siblingSet.Where(candidate => candidate.TabId != tab.TabId).ToList();
            tree.Add(tab);

            Portal? portal = await _portals.GetAsync(portalId, includeAliases: false, cancellationToken)
                .ConfigureAwait(false);
            RecomputeTree(tree, portal?.AdminTabId);
        }
        else
        {
            // A host-level page is not portal-scoped and the page repository exposes no host-wide
            // listing, so the ordering of the host tree is left untouched and only this page's own
            // depth and path are refreshed from its parent chain.
            Tab? parent = tab.ParentId is int hostParentId
                ? await _tabs.GetAsync(hostParentId, cancellationToken).ConfigureAwait(false)
                : null;

            tab.Level = parent is null ? RootLevel : parent.Level + 1;
            tab.TabPath = await BuildHostTabPathAsync(tab, cancellationToken).ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (tab.PortalId is int invalidatedPortalId)
        {
            _cache.InvalidateTabs(invalidatedPortalId);
        }
        else
        {
            _cache.InvalidateHost();
        }

        bool hasChildren = await HasChildrenAsync(tab, cancellationToken).ConfigureAwait(false);
        return Result<TabDetailDto>.Success(TabMappings.ToDetail(tab, hasChildren));
    }

    /// <summary>
    /// Reads a portal's pages and projects them onto list rows, resolving the has-children flag for
    /// the whole set in one round trip.
    /// </summary>
    /// <param name="portalId">The portal whose pages are read.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>The portal's pages as list rows, in the hierarchy order the repository guarantees.</returns>
    private async Task<IReadOnlyList<TabListItemDto>> ReadPortalTabsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        // Pages in the recycle bin are excluded: this listing exists so that a caller can build the
        // navigation tree and so that the module screens can offer a placement target, and neither
        // use admits a deleted page.
        IReadOnlyList<Tab> tabs = await _tabs
            .ListAsync(portalId, includeDeleted: false, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyCollection<int> parentIds = await _tabs
            .ListParentTabIdsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        var parents = new HashSet<int>(parentIds);

        // The repository documents its result as already being in hierarchy order, so it is
        // deliberately not re-sorted here.
        var rows = new List<TabListItemDto>(tabs.Count);
        foreach (Tab tab in tabs)
        {
            rows.Add(TabMappings.ToListItem(tab, parents.Contains(tab.TabId)));
        }

        return rows;
    }

    /// <summary>
    /// Determines whether any page names <paramref name="tab"/> as its parent.
    /// </summary>
    /// <param name="tab">The page to test.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns><see langword="true"/> when at least one page is a child of <paramref name="tab"/>.</returns>
    private async Task<bool> HasChildrenAsync(Tab tab, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> parentIds = await _tabs
            .ListParentTabIdsAsync(tab.PortalId, cancellationToken)
            .ConfigureAwait(false);

        return parentIds.Contains(tab.TabId);
    }

    /// <summary>
    /// Rejects a requested parent that does not exist, belongs to another portal, or would create a
    /// cycle.
    /// </summary>
    /// <param name="tab">The page being updated.</param>
    /// <param name="requestedParentId">
    /// The requested parent, or <see langword="null"/> when the page becomes a root-level page.
    /// </param>
    /// <param name="portalTabs">
    /// Every page of the owning portal, used to walk ancestry without further round trips. Empty for
    /// a host-level page, in which case ancestry is walked through the repository instead.
    /// </param>
    /// <param name="cancellationToken">Token observed while any read is in flight.</param>
    /// <returns>
    /// A failed result to return to the caller, or <see langword="null"/> when the requested parent
    /// is acceptable.
    /// </returns>
    private async Task<Result<TabDetailDto>?> ValidateParentAsync(
        Tab tab,
        int? requestedParentId,
        IReadOnlyList<Tab> portalTabs,
        CancellationToken cancellationToken)
    {
        if (requestedParentId is not int parentId)
        {
            return null;
        }

        if (parentId == tab.TabId)
        {
            return Result<TabDetailDto>.Failure(
                ParentCycleCode,
                "A page cannot be its own parent.");
        }

        Tab? parent = portalTabs.FirstOrDefault(candidate => candidate.TabId == parentId)
            ?? await _tabs.GetAsync(parentId, cancellationToken).ConfigureAwait(false);

        if (parent is null)
        {
            return Result<TabDetailDto>.Failure(
                ParentNotFoundCode,
                $"No page bears identifier {parentId}, so it cannot be used as a parent.");
        }

        if (parent.PortalId != tab.PortalId)
        {
            // Tenant isolation must be asserted here because the identifier now arrives in a request
            // body rather than from a portal-filtered picker on a server-rendered page.
            return Result<TabDetailDto>.Failure(
                ParentCrossPortalCode,
                "The requested parent page belongs to a different portal.");
        }

        bool isDescendant = await IsDescendantAsync(parent, tab.TabId, portalTabs, cancellationToken)
            .ConfigureAwait(false);
        if (isDescendant)
        {
            // MIGRATION: the legacy screen ran the same ancestry walk but, when it tripped, silently
            // abandoned the save and rendered nothing - a discovered defect a user could mistake for
            // success. The rejection is preserved; the silence is not.
            return Result<TabDetailDto>.Failure(
                ParentCycleCode,
                "The requested parent page is a descendant of the page being updated.");
        }

        return null;
    }

    /// <summary>
    /// Walks upward from <paramref name="candidate"/> and reports whether
    /// <paramref name="ancestorTabId"/> is found on the way to the root.
    /// </summary>
    /// <param name="candidate">The page whose ancestry is walked.</param>
    /// <param name="ancestorTabId">The page being sought among the ancestors.</param>
    /// <param name="portalTabs">Every page of the owning portal, or an empty set for a host page.</param>
    /// <param name="cancellationToken">Token observed while any read is in flight.</param>
    /// <returns><see langword="true"/> when the sought page is an ancestor of the candidate.</returns>
    private async Task<bool> IsDescendantAsync(
        Tab candidate,
        int ancestorTabId,
        IReadOnlyList<Tab> portalTabs,
        CancellationToken cancellationToken)
    {
        Dictionary<int, Tab> byId = portalTabs.ToDictionary(entry => entry.TabId);

        // The walk is bounded by the number of pages it may legitimately traverse, so a pre-existing
        // cycle in stored data terminates the loop instead of hanging the request.
        int guard = byId.Count + 1;
        Tab? current = candidate;

        while (current is not null && guard-- > 0)
        {
            if (current.TabId == ancestorTabId)
            {
                return true;
            }

            if (current.ParentId is not int nextId)
            {
                return false;
            }

            if (!byId.TryGetValue(nextId, out Tab? next))
            {
                next = await _tabs.GetAsync(nextId, cancellationToken).ConfigureAwait(false);
            }

            current = next;
        }

        return false;
    }

    /// <summary>
    /// Recomputes depth, order and materialised path for every page of a portal.
    /// </summary>
    /// <param name="tabs">Every page of the portal, including pages in the recycle bin.</param>
    /// <param name="adminTabId">
    /// The portal's administration page, or <see langword="null"/> when the portal has none, in which
    /// case no page is placed in the administration order band.
    /// </param>
    /// <remarks>
    /// MIGRATION: reproduces the end state of the legacy reordering routine (L550-L775) - a
    /// hierarchy-ordered walk assigning orders in steps of two from two independent counters, the
    /// ordinary band seeded at -1 and the administration band at 9,999 - without reproducing its
    /// flat-list splice. The legacy classification of the administration band is preserved verbatim:
    /// a page qualifies only if it is the administration page itself or one of its immediate
    /// children, so a deeper descendant is renumbered in the ordinary band exactly as before.
    /// </remarks>
    private static void RecomputeTree(IReadOnlyList<Tab> tabs, int? adminTabId)
    {
        var childrenByParent = new Dictionary<int, List<Tab>>();
        var roots = new List<Tab>();

        foreach (Tab tab in tabs)
        {
            if (tab.ParentId is int parentId && tabs.Any(candidate => candidate.TabId == parentId))
            {
                if (!childrenByParent.TryGetValue(parentId, out List<Tab>? bucket))
                {
                    bucket = new List<Tab>();
                    childrenByParent[parentId] = bucket;
                }

                bucket.Add(tab);
            }
            else
            {
                // A page whose stored parent is absent from the portal is treated as a root, which
                // keeps an orphaned row reachable instead of dropping it out of the walk.
                roots.Add(tab);
            }
        }

        SortSiblings(roots);
        foreach (List<Tab> bucket in childrenByParent.Values)
        {
            SortSiblings(bucket);
        }

        int desktopOrder = DesktopTabOrderSeed;
        int adminOrder = AdminTabOrderSeed;

        void Walk(Tab tab, int level, string parentPath)
        {
            tab.Level = level;

            bool inAdminBand = adminTabId is int adminId
                && (tab.TabId == adminId || tab.ParentId == adminId);

            if (inAdminBand)
            {
                adminOrder += TabOrderStep;
                tab.TabOrder = adminOrder;
            }
            else
            {
                desktopOrder += TabOrderStep;
                tab.TabOrder = desktopOrder;
            }

            string path = parentPath + TabPathSeparator + StripNonWord(tab.TabName);
            tab.TabPath = path;

            if (childrenByParent.TryGetValue(tab.TabId, out List<Tab>? children))
            {
                foreach (Tab child in children)
                {
                    Walk(child, level + 1, path);
                }
            }
        }

        foreach (Tab root in roots)
        {
            Walk(root, RootLevel, string.Empty);
        }
    }

    /// <summary>
    /// Orders a set of siblings the way the legacy routine did.
    /// </summary>
    /// <param name="siblings">The siblings to order in place.</param>
    /// <remarks>
    /// A stored order of zero meant "unset" and sorted after the explicitly ordered siblings, which
    /// the legacy routine expressed by substituting 999 before sorting. Name is the tie-breaker so
    /// that the walk is deterministic when two siblings share an order.
    /// </remarks>
    private static void SortSiblings(List<Tab> siblings)
    {
        siblings.Sort((left, right) =>
        {
            int leftOrder = left.TabOrder == UnsetTabOrder ? UnsetTabOrderSortValue : left.TabOrder;
            int rightOrder = right.TabOrder == UnsetTabOrder ? UnsetTabOrderSortValue : right.TabOrder;

            int byOrder = leftOrder.CompareTo(rightOrder);
            if (byOrder != 0)
            {
                return byOrder;
            }

            int byName = string.Compare(left.TabName, right.TabName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.TabId.CompareTo(right.TabId);
        });
    }

    /// <summary>
    /// Builds the materialised hierarchy path of a host-level page by walking its parent chain.
    /// </summary>
    /// <param name="tab">The page whose path is built.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>The page's materialised path.</returns>
    private async Task<string> BuildHostTabPathAsync(Tab tab, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        int? ancestorId = tab.ParentId;

        // The walk is bounded so that a pre-existing cycle in stored host pages cannot hang a
        // request. The bound is generous relative to any legitimate host page depth.
        const int MaxAncestorWalk = 64;
        for (int step = 0; step < MaxAncestorWalk && ancestorId is int currentId; step++)
        {
            Tab? ancestor = await _tabs.GetAsync(currentId, cancellationToken).ConfigureAwait(false);
            if (ancestor is null)
            {
                break;
            }

            segments.Insert(0, StripNonWord(ancestor.TabName));
            ancestorId = ancestor.ParentId;
        }

        segments.Add(StripNonWord(tab.TabName));
        return TabPathSeparator + string.Join(TabPathSeparator, segments);
    }

    /// <summary>
    /// Removes every non-word character from a path segment.
    /// </summary>
    /// <param name="value">The raw page name.</param>
    /// <returns>The name reduced to word characters.</returns>
    /// <remarks>
    /// MIGRATION: reproduces the excluded HTML utility's <c>StripNonWord</c> (L327) for the single
    /// use the page write path has for it. The legacy expression was <c>\W*</c> with an empty
    /// replacement, whose output is identical to removing each non-word character individually.
    /// </remarks>
    private static string StripNonWord(string value) => NonWordPattern.Replace(value, string.Empty);
}
