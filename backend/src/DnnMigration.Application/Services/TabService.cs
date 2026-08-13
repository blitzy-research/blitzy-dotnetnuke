using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>Reads and updates the page hierarchy of a portal.</summary>
/// <remarks>
/// <para>
/// Page ordering. The legacy reordering routine spliced a flat, hierarchy-ordered list and then renumbered
/// every row in steps of two, seeding ordinary pages from -1 and pages in the administration band from
/// 9,999 so that administration pages always sorted last.
/// </para>
/// <para>
/// MIGRATION: two legacy read behaviours are deliberately absent. The single-page reader took an
/// <c>ignoreCache</c> argument that let a caller force a database round trip; no such flag crosses this
/// contract, so the read path is uniform and caching stays entirely this service's business.
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

    /// <summary>Resource type recorded on every page audit event.</summary>
    private const string TabResourceType = "Tab";

    /// <summary>
    /// Legacy cache key shape for a portal's page collection, preserved verbatim from
    /// <c>DataCache.TabCacheKey</c>.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the infrastructure cache implementation declares the identical shape, but declares it
    /// <see langword="internal"/> to its own assembly, and the application layer references the domain
    /// layer alone - so the literal cannot be shared and has to be restated here.
    /// </remarks>
    private const string TabCacheKeyFormat = "Tabs{0}";

    /// <summary>
    /// Legacy base cache timeout in minutes for a portal's page collection, preserved verbatim from
    /// <c>DataCache.TabCacheTimeOut</c>. The effective timeout is this value multiplied by the
    /// installation-wide performance setting.
    /// </summary>
    private const int TabCacheTimeOutMinutes = 20;

    /// <summary>
    /// Separator that opens every segment of the materialised hierarchy path, preserved from the legacy
    /// path generator.
    /// </summary>
    private const string TabPathSeparator = "//";

    /// <summary>
    /// Stored order value the legacy reordering routine treated as "unset", sorting such a page after its
    /// explicitly ordered siblings.
    /// </summary>
    private const int UnsetTabOrder = 0;

    /// <summary>
    /// Order the legacy reordering routine substituted for <see cref="UnsetTabOrder"/> while sorting.
    /// </summary>
    private const int UnsetTabOrderSortValue = 999;

    /// <summary>Seed of the ordinary page order counter, preserved from the legacy routine.</summary>
    private const int DesktopTabOrderSeed = -1;

    /// <summary>
    /// Seed of the administration page order counter, preserved from the legacy routine, which chose it so
    /// that administration pages always sort after the five thousand ordinary pages a portal could hold.
    /// </summary>
    private const int AdminTabOrderSeed = 9999;

    /// <summary>Step by which the legacy routine advanced either order counter.</summary>
    private const int TabOrderStep = 2;

    /// <summary>Depth assigned to a page that has no parent.</summary>
    private const int RootLevel = 0;

    /// <summary>Deepest page hierarchy the stored path can represent, counted in levels including the root.</summary>
    /// <remarks>
    /// Read from the column rather than chosen. <c>Tabs.TabPath</c> holds 255 characters and every level
    /// contributes at least the two-character separator to the assembled path, so 127 levels is the deepest
    /// hierarchy whose path is storable at all, whatever the page names are.
    /// </remarks>
    private const int MaximumTabDepth = 127;

    /// <summary>
    /// Device names the legacy page-management screen refused, reproduced from
    /// <c>Website/admin/Tabs/ManageTabs.ascx.vb</c> line 272. The legacy pattern listed <c>^CON$</c> twice,
    /// which is redundant and is therefore stated once here.
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
    private readonly ICurrentUser _currentUser;

    /// <summary>Answers which of a tenant's pages the current caller may act on.</summary>
    /// <remarks>
    /// Read ONLY by the listing, and only to narrow what it returns. Nothing here decides admission - that
    /// is the endpoint's policy - so this is a projection concern rather than an authorisation one.
    /// </remarks>
    private readonly IPermissionService _permissions;
    private readonly IAuditSink _audit;
    private readonly CachingOptions _caching;

    /// <summary>Initialises a new instance of the <see cref="TabService"/> class.</summary>
    /// <param name="tabs">Page repository.</param>
    /// <param name="portals">
    /// Portal repository, consulted for two distinct reasons: to prove a portal exists before listing its
    /// pages, and to read the portal's administration page so that the renumbering pass can classify the
    /// administration band exactly as the legacy routine did.
    /// </param>
    /// <param name="unitOfWork">Commits the page tree in a single transaction.</param>
    /// <param name="cache">Absorbs the legacy page-collection cache.</param>
    /// <param name="currentUser">
    /// Identifies the caller, so an audit record names the account that changed the page rather than
    /// repeating a value the request supplied.
    /// </param>
    /// <param name="audit">Records the page change under the legacy event name.</param>
    /// <param name="permissions">
    /// Resolves which pages the current caller may act on, so the listing can be narrowed to them.
    /// </param>
    /// <param name="caching">Bound caching configuration.</param>
    public TabService(
        ITabRepository tabs,
        IPortalRepository portals,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        ICurrentUser currentUser,
        IPermissionService permissions,
        IAuditSink audit,
        CachingOptions caching)
    {
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
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

        string cacheKey = string.Format(CultureInfo.InvariantCulture, TabCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(TabCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // The legacy entry was written and read as a PERSISTENT cache item, a flag that survived an
        // application-domain recycle by spilling to disk.
        IReadOnlyList<TabListItemDto> rows = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadPortalTabsAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadPortalTabsAsync(portalId, cancellationToken).ConfigureAwait(false);

        // ⚠ FILTERED AFTER THE CACHE READ, NEVER BEFORE IT, AND THE ORDER IS THE WHOLE CORRECTNESS
        // ARGUMENT. The entry above is keyed by tenant alone, so it must hold the tenant's rows and nothing
        // caller-specific; narrowing before the write would store one caller's permitted subset under a key
        // every caller reads, and the next caller would be served that subset as though it were the
        // tenant's page set.
        IReadOnlyList<int> permitted = await PermittedTabIdsAsync(portalId, rows, cancellationToken)
            .ConfigureAwait(false);

        if (permitted.Count == rows.Count)
        {
            // Nothing was withheld, so the cached instance is returned as it stands rather than copied.
            return Result<IReadOnlyList<TabListItemDto>>.Success(rows);
        }

        var allowed = new HashSet<int>(permitted);

        // The navigation order the read produced is preserved: a child's position is meaningful only relative
        // to the parent that precedes it, so the rows are filtered in place rather than re-ordered.
        return Result<IReadOnlyList<TabListItemDto>>.Success(
            rows.Where(row => allowed.Contains(row.TabId)).ToList());
    }

    /// <summary>Resolves which of the listed pages the current caller may act on.</summary>
    /// <param name="portalId">The tenant the pages belong to.</param>
    /// <param name="rows">The tenant's pages, in navigation order.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The identifiers of the pages the caller may act on.</returns>
    /// <remarks>
    /// The permission service answers with every named page for a caller who administers the tenant or the
    /// installation, and with the caller's EDIT-granted pages otherwise, in ONE evaluation over the whole
    /// set rather than one per page - so this narrowing does not make the listing's cost scale with the
    /// tenant's page tree.
    /// </remarks>
    private async Task<IReadOnlyList<int>> PermittedTabIdsAsync(
        int portalId,
        IReadOnlyList<TabListItemDto> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        Result<IReadOnlyList<int>> permitted = await _permissions
            .ListTabsWithPermissionAsync(
                portalId,
                _currentUser.UserId,
                rows.Select(row => row.TabId).ToList(),
                PermissionKey.EDIT,
                cancellationToken)
            .ConfigureAwait(false);

        // A failed evaluation withholds every row rather than offering them all. This listing is offered as
        // a set of CHOICES, so the closed answer is the safe one: an unresolvable permission state must not
        // present a placement target the create action would then refuse.
        return permitted.IsSuccess ? permitted.Value : [];
    }

    /// <inheritdoc />
    public async Task<Result<TabDetailDto?>> GetTabAsync(int tabId, CancellationToken cancellationToken = default)
    {
        Tab? tab = await _tabs.GetByIdAsync(tabId, cancellationToken).ConfigureAwait(false);
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

        Tab? tab = await _tabs.GetByIdAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null)
        {
            // Unlike the read member, a mutation names a resource it expects to act upon, so a
            // missing target is an expected failure rather than an empty success.
            return Result<TabDetailDto>.Failure(NotFoundCode, $"No page bears identifier {tabId}.");
        }

        if (string.IsNullOrWhiteSpace(request.TabName))
        {
            throw new DomainException("UpdateTabRequest.TabName was blank; the page name is required.")
            {
                PublicDetail = "A page name is required.",
            };
        }

        if (ReservedNamePattern.IsMatch(request.TabName))
        {
            return Result<TabDetailDto>.Failure(
                NameReservedCode,
                "The page name is a reserved device name and cannot be used.");
        }

        // The whole portal is needed for the ancestry test and for the renumbering pass that follows, so it
        // is read once here and reused. Pages in the recycle bin are included because they still occupy
        // positions in the legacy ordering, exactly as the legacy list did.
        IReadOnlyList<Tab> siblingSet = tab.PortalId is int owningPortalId
            ? await _tabs.GetByPortalIdAsync(owningPortalId, cancellationToken).ConfigureAwait(false)
            : Array.Empty<Tab>();

        Result<TabDetailDto>? parentRejection =
            await ValidateParentAsync(tab, request.ParentId, siblingSet, cancellationToken).ConfigureAwait(false);
        if (parentRejection is not null)
        {
            return parentRejection;
        }

        // Read BEFORE the update is applied, because the mapper writes onto the tracked aggregate and the
        // former values are unrecoverable afterwards. They are carried on the audit record only when they
        // actually changed, so a record never asserts a rename that did not happen.
        int? previousParentId = tab.ParentId;

        // THE FORMER DELETE FLAG IS CAPTURED FOR THE SAME REASON AND FOR A LARGER PURPOSE. The mapper
        // assigns IsDeleted from the request, so this member is the boundary at which a page is recycled
        // and at which a recycled page is restored - and the legacy vocabulary has a distinct event name
        // for each of those, TAB_SENT_TO_RECYCLE_BIN and TAB_RESTORED. Recording all three transitions as
        // TAB_UPDATED made a recycling and a restoration indistinguishable from a title change, which is
        // exactly the distinction an operator asking "who took this page down" needs.
        bool previouslyDeleted = tab.IsDeleted;

        TabMappings.ApplyUpdate(tab, request);

        if (tab.PortalId is int portalId)
        {
            // The tracked aggregate and the listed instance may be distinct objects, so the listed
            // set is rebuilt with the tracked page substituted in before the tree is recomputed.
            List<Tab> tree = siblingSet.Where(candidate => candidate.TabId != tab.TabId).ToList();
            tree.Add(tab);

            // The legacy routine recorded every page's position in a hash table before it spliced the
            // working list, then persisted only the pages whose recorded values had actually moved.
            IReadOnlyDictionary<int, TabPosition> positions = SnapshotPositions(tree, tab.TabId);

            Portal? portal = await _portals.GetByIdAsync(portalId, includeAliases: false, cancellationToken)
                .ConfigureAwait(false);

            if (portal is not null && IsProtectedSpecialPage(portal, tab.TabId))
            {
                tab.DisableLink = false;
            }

            RecomputeTree(tree, portal?.AdminTabId);

            await StageMovedPagesAsync(tree, positions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A host-level page is not portal-scoped and the page repository exposes no host-wide listing,
            // so the ordering of the host tree is left untouched and only this page's own depth and path
            // are refreshed from its parent chain.
            Tab? parent = tab.ParentId is int hostParentId
                ? await _tabs.GetByIdAsync(hostParentId, cancellationToken).ConfigureAwait(false)
                : null;

            tab.Level = parent is null ? RootLevel : parent.Level + 1;
            tab.TabPath = await BuildHostTabPathAsync(tab, cancellationToken).ConfigureAwait(false);
        }

        // The edited page is staged through the wide update rather than the positional one, mirroring the
        // legacy pair exactly: the reordering routine reached the four-column positional procedure for each
        // page it moved, and its caller then reached the nineteen-column procedure for the page actually
        // being edited.
        await _tabs.UpdateAsync(tab, cancellationToken).ConfigureAwait(false);

        // One commit for the whole tree. The renumbering pass touches every page of the portal, and the
        // legacy path issued those writes as independent statements with no enclosing transaction, so a
        // failure part-way through left the hierarchy renumbered inconsistently.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (tab.PortalId is int invalidatedPortalId)
        {
            _cache.InvalidateTabs(invalidatedPortalId);
            _cache.InvalidatePortal(invalidatedPortalId);
        }
        else
        {
            // A host-level page has no portal, and the legacy code had no way to say so it passed the
            // page's portal identifier straight through, which for a host page was the integer sentinel -1,
            // and so evicted the cache entries of the portal whose real identifier is -1.
            _cache.InvalidateHost();
        }

        // Recorded AFTER the commit, so no record can describe a change that was rolled back, and only the
        // stable identifiers and the ancestry change, which is the one that moves other pages as a side
        // effect.
        Dictionary<string, string?> pageFacts = new(StringComparer.Ordinal)
        {
            ["ParentId"] = tab.ParentId?.ToString(CultureInfo.InvariantCulture),
            ["IsVisible"] = tab.IsVisible.ToString(),
            ["IsDeleted"] = tab.IsDeleted.ToString(),
        };

        if (previousParentId != tab.ParentId)
        {
            pageFacts["PreviousParentId"] = previousParentId?.ToString(CultureInfo.InvariantCulture);
        }

        (string eventName, string operation) = (previouslyDeleted, tab.IsDeleted) switch
        {
            (false, true) => (AuditEventNames.TabSentToRecycleBin, "Recycle"),
            (true, false) => (AuditEventNames.TabRestored, "Restore"),
            _ => (AuditEventNames.TabUpdated, "Revise"),
        };

        pageFacts["Operation"] = operation;

        AuditEvent record = new(eventName)
        {
            PortalId = tab.PortalId,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ResourceType = TabResourceType,
            ResourceId = tab.TabId.ToString(CultureInfo.InvariantCulture),
            Properties = pageFacts,
        };

        _audit.Record(record);

        bool hasChildren = await HasChildrenAsync(tab, cancellationToken).ConfigureAwait(false);
        return Result<TabDetailDto>.Success(TabMappings.ToDetail(tab, hasChildren));
    }

    /// <summary>
    /// Reports whether a page is one of the five portal-designated pages whose link cannot be disabled.
    /// </summary>
    /// <param name="portal">Owning portal.</param>
    /// <param name="tabId">Page identifier.</param>
    /// <returns><see langword="true"/> when the identifier occupies any protected special-page role.</returns>
    private static bool IsProtectedSpecialPage(Portal portal, int tabId)
    {
        return portal.AdminTabId == tabId
            || portal.SplashTabId == tabId
            || portal.HomeTabId == tabId
            || portal.LoginTabId == tabId
            || portal.UserTabId == tabId;
    }

    /// <summary>
    /// Reads a portal's pages and projects them onto list rows, resolving the has-children flag for the
    /// whole set in one round trip.
    /// </summary>
    /// <param name="portalId">The portal whose pages are read.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>
    /// The portal's pages as list rows, in the hierarchy order the repository guarantees. <b>Every page is
    /// projected, including pages in the recycle bin</b>, each carrying its own <c>IsDeleted</c> flag; no
    /// row is withheld here.
    /// </returns>
    private async Task<IReadOnlyList<TabListItemDto>> ReadPortalTabsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        // PAGES IN THE RECYCLE BIN ARE INCLUDED, and that is the authoritative answer rather than a
        // relaxation.
        IReadOnlyList<Tab> tabs = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
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

    /// <summary>Determines whether any page names <paramref name="tab"/> as its parent.</summary>
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
    /// Rejects a requested parent that does not exist, belongs to another portal, or would create a cycle.
    /// </summary>
    /// <param name="tab">The page being updated.</param>
    /// <param name="requestedParentId">
    /// The requested parent, or <see langword="null"/> when the page becomes a root-level page.
    /// </param>
    /// <param name="portalTabs">
    /// Every page of the owning portal, used to walk ancestry without further round trips.
    /// </param>
    /// <param name="cancellationToken">Token observed while any read is in flight.</param>
    /// <returns>
    /// A failed result to return to the caller, or <see langword="null"/> when the requested parent is
    /// acceptable.
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
            ?? await _tabs.GetByIdAsync(parentId, cancellationToken).ConfigureAwait(false);

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
            return Result<TabDetailDto>.Failure(
                ParentCycleCode,
                "The requested parent page is a descendant of the page being updated.");
        }

        return null;
    }

    /// <summary>
    /// Walks upward from <paramref name="candidate"/> and reports whether <paramref name="ancestorTabId"/>
    /// is found on the way to the root.
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
                next = await _tabs.GetByIdAsync(nextId, cancellationToken).ConfigureAwait(false);
            }

            current = next;
        }

        return false;
    }

    /// <summary>
    /// Records the position of every page in a portal's tree so that the pages the recomputation actually
    /// moves can be told apart from the pages it leaves where they were.
    /// </summary>
    /// <param name="tabs">Every page of the portal, as it stands before the recomputation.</param>
    /// <param name="excludedTabId">
    /// The page being edited, which is omitted because it is staged unconditionally and has already had its
    /// parent reassigned from the request.
    /// </param>
    /// <returns>Each page's position, keyed by page identifier.</returns>
    private static IReadOnlyDictionary<int, TabPosition> SnapshotPositions(
        IReadOnlyList<Tab> tabs,
        int excludedTabId)
    {
        var positions = new Dictionary<int, TabPosition>(tabs.Count);

        foreach (Tab tab in tabs)
        {
            if (tab.TabId == excludedTabId)
            {
                continue;
            }

            positions[tab.TabId] = new TabPosition(tab.TabOrder, tab.Level, tab.ParentId, tab.TabPath);
        }

        return positions;
    }

    /// <summary>Stages a positional write for every page the recomputation moved, and for no other page.</summary>
    /// <param name="tabs">Every page of the portal, as it stands after the recomputation.</param>
    /// <param name="positions">The positions recorded before the recomputation.</param>
    /// <param name="cancellationToken">Token observed while the writes are staged.</param>
    /// <returns>A task that completes once every moved page has been staged.</returns>
    /// <remarks>
    /// MIGRATION: the materialised path is compared alongside those three, which the legacy comparison did
    /// not do - because the legacy code propagated a changed path through an entirely separate recursive
    /// pass over the edited page's children, rewriting each one whose path had changed and recursing
    /// (<c>UpdateChildTabPath</c>, L306-L320).
    /// </remarks>
    private async Task StageMovedPagesAsync(
        IReadOnlyList<Tab> tabs,
        IReadOnlyDictionary<int, TabPosition> positions,
        CancellationToken cancellationToken)
    {
        foreach (Tab tab in tabs)
        {
            if (!positions.TryGetValue(tab.TabId, out TabPosition recorded))
            {
                // Either the page being edited, which is staged by its own wide write, or a page that
                // was not present before the recomputation. Neither has a position to compare.
                continue;
            }

            var current = new TabPosition(tab.TabOrder, tab.Level, tab.ParentId, tab.TabPath);
            if (current == recorded)
            {
                continue;
            }

            await _tabs.UpdateOrderAsync(tab, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Recomputes depth, order and materialised path for every page of a portal.</summary>
    /// <param name="tabs">Every page of the portal, including pages in the recycle bin.</param>
    /// <param name="adminTabId">
    /// The portal's administration page, or <see langword="null"/> when the portal has none, in which case
    /// no page is placed in the administration order band.
    /// </param>
    /// <remarks>
    /// Reproduces the end state of the legacy reordering routine - a hierarchy-ordered walk assigning
    /// orders in steps of two from two independent counters, the ordinary band seeded at -1 and the
    /// administration band at 9,999 - without reproducing its flat-list splice.
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

        // The traversal is ITERATIVE, and the recursion it replaces was a denial-of-service vector rather
        // than a style preference.
        var pending = new Stack<(Tab Tab, int Level, string ParentPath)>();

        for (int index = roots.Count - 1; index >= 0; index--)
        {
            pending.Push((roots[index], RootLevel, string.Empty));
        }

        while (pending.Count > 0)
        {
            (Tab tab, int level, string parentPath) = pending.Pop();

            // The depth limit is now a policy bound rather than a crash guard, since the traversal above no
            // longer consumes stack per level.
            if (level - RootLevel >= MaximumTabDepth)
            {
                throw new DomainException(
                    FormattableString.Invariant(
                        $"Page hierarchy depth exceeds {MaximumTabDepth}; TabPath cannot be stored."))
                {
                    PublicDetail = FormattableString.Invariant(
                        $"The page hierarchy is deeper than {MaximumTabDepth} levels.")
                        + " That is more than a stored page path can express.",
                };
            }

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
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    pending.Push((children[index], level + 1, path));
                }
            }
        }
    }

    /// <summary>Orders a set of siblings the way the legacy routine did.</summary>
    /// <param name="siblings">The siblings to order in place.</param>
    /// <remarks>
    /// A stored order of zero meant "unset" and sorted after the explicitly ordered siblings, which the
    /// legacy routine expressed by substituting 999 before sorting. Name is the tie-breaker so that the
    /// walk is deterministic when two siblings share an order.
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

    /// <summary>Builds the materialised hierarchy path of a host-level page by walking its parent chain.</summary>
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
            Tab? ancestor = await _tabs.GetByIdAsync(currentId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Removes every non-word character from a path segment.</summary>
    /// <param name="value">The raw page name.</param>
    /// <returns>The name reduced to word characters.</returns>
    private static string StripNonWord(string value) => NonWordPattern.Replace(value, string.Empty);

    /// <summary>A page's position in its portal's hierarchy: the four values the positional write persists.</summary>
    /// <param name="TabOrder">The page's sequence number among the portal's pages.</param>
    /// <param name="Level">The page's depth, where zero is the root.</param>
    /// <param name="ParentId">The page's parent, or <see langword="null"/> for a root-level page.</param>
    /// <param name="TabPath">The page's materialised hierarchy path.</param>
    /// <remarks>
    /// Replaces the legacy routine's nested ordering helper class, which carried the first three of these
    /// values inside a hash table keyed by page identifier. A record struct gives the value equality the
    /// comparison needs without a class allocation per page, and declaring it nested and private keeps it
    /// out of the layer's public surface, where it has no business being.
    /// </remarks>
    private readonly record struct TabPosition(int TabOrder, int Level, int? ParentId, string? TabPath);
}
