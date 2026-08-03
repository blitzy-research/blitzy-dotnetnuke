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
/// MIGRATION: the four markers the legacy reordering routine seeded at L553-L556 are loop
/// bookkeeping over a flat working list, not contract values, and not one of them is an entity
/// identifier. <c>intFromIndex = -1</c> and <c>intToIndex = -1</c> each meant "this LIST POSITION has
/// not been located"; <c>intNewParentIndex = 0</c> was simply the default insertion anchor at
/// position zero; and <c>intOldParentId = -2</c> meant "the page is absent from the working list
/// altogether". A fifth meaning rode on the parameter itself: <c>NewParentId = -1</c> meant "root"
/// and <c>NewParentId = -2</c> meant "already hard-deleted", guarded at L592, L611 and L613. That
/// <c>-2</c> reached the routine from exactly one call site - <c>DeleteTab</c> at L452, and only
/// after the row had already been removed permanently - so with no delete endpoint in this migration
/// the path is unreachable here, while "root" is carried by a null parent rather than by any number.
/// None of the five is reproduced and none may be reintroduced: <c>Tabs.TabID</c> is
/// <c>IDENTITY(0, 1)</c> and <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so <c>0</c> and
/// <c>-1</c> are both real stored identifiers in this schema.
/// </para>
/// <para>
/// MIGRATION: the legacy routine's <c>Level</c> and <c>Order</c> arguments were RELATIVE STEPS
/// rather than absolute values - the page-management screen passed -1 and +1 to outdent and indent a
/// page (<c>Tabs.ascx.vb</c> L188, L190) and -1 and +1 to move it up and down among its siblings
/// (L223, L225), while every other caller passed zero for both. This service reproduces the zero
/// case only, which is exactly what <c>UpdateTab</c> itself passed (L787), because the migration
/// defines no page administration screen and therefore no nudge affordance: depth and sibling order
/// are consequences of the parent a caller states, never inputs. The routine's optional
/// <c>NewTab</c> tail is unreachable for the same kind of reason - its single caller was
/// <c>AddTab</c> at L351, and creation is out of contract - which is why the update request carries
/// no property for it.
/// </para>
/// <para>
/// MIGRATION: two legacy read behaviours are deliberately absent. The single-page reader took an
/// <c>ignoreCache</c> argument (L467) that let a caller force a database round trip; no such flag
/// crosses this contract, so the read path is uniform and caching stays entirely this service's
/// business. That reader also recovered an unknown portal by asking
/// <c>PortalController.GetPortalDictionary()</c> to map a page identifier back to a portal. The
/// dictionary member is not ported, and the reverse lookup is unnecessary here: a page row carries
/// its own portal, which the detail projection returns to the caller.
/// </para>
/// <para>
/// MIGRATION: the legacy administration-band test also matched the installation's host root page and
/// that page's children (L751-L753). Reproducing it would be dead code. The terminal <c>GetTabs</c>
/// procedure returns host rows only when it is asked for a null portal
/// (<c>04.04.00.SqlDataProvider</c> L440-L447), so a host page can never appear in a portal's page
/// list, and the portal entity carries no host-root column for that same reason. The band is
/// therefore classified against the portal's administration page alone.
/// </para>
/// <para>
/// MIGRATION: the legacy page editor wrote an audit entry of its own after a successful save
/// (<c>ManageTabs.ascx.vb</c> L309, event type <c>TAB_UPDATED</c>). No logger is injected here to
/// reproduce it, and that is a deliberate boundary rather than an oversight: this project references
/// FluentValidation and the domain layer and nothing else, and structured logging belongs to the API
/// layer, where request logging and its correlation identifier already record every mutation. The
/// omission is recorded rather than silently absorbed.
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
    /// <remarks>
    /// MIGRATION: the infrastructure cache implementation declares the identical shape, but declares
    /// it <see langword="internal"/> to its own assembly, and the application layer references the
    /// domain layer alone - so the literal cannot be shared and has to be restated here. It must stay
    /// byte-identical to that declaration and must be formatted with the invariant culture, because
    /// the eviction member this service calls after an update rebuilds the same key from the same
    /// shape; a divergence would not fail to compile, it would silently stop invalidating.
    /// </remarks>
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
    /// <param name="portals">
    /// Portal repository, consulted for two distinct reasons: to prove a portal exists before
    /// listing its pages, and to read the portal's administration page so that the renumbering pass
    /// can classify the administration band exactly as the legacy routine did.
    /// </param>
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

        string cacheKey = string.Format(CultureInfo.InvariantCulture, TabCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(TabCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: the legacy reader skipped the database entirely when the resolved timeout was
        // zero, because it judged the load too costly to repeat per request. That shortcut is not
        // reproduced - the read always runs - but the "caching disabled" branch itself is, so a zero
        // multiplier genuinely bypasses the store instead of writing an entry that expires at once.
        //
        // MIGRATION: the legacy entry was written and read as a PERSISTENT cache item, a flag that
        // survived an application-domain recycle by spilling to disk. The domain cache abstraction
        // offers no such flag and the target holds this entry in memory only, so a process restart
        // now costs one repopulating read where the legacy installation paid none. That is a
        // deliberate divergence: durability of a derived, cheaply rebuilt projection is not worth an
        // out-of-process store, and the legacy flag existed to serve a hosting model - assembly
        // probing under a recycling worker process - that this stack does not have.
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

        // MIGRATION: this reproduces the legacy screen's own required-name rejection, which lived in
        // the markup rather than the code-behind - Website/admin/Tabs/managetabs.ascx L36-L37 declares
        // a required-field validator over the page-name box carrying the message "Tab Name Is
        // Required" - so an absent name never reached the legacy controller at all. It is behaviour
        // preserved, not behaviour invented, and it is enforced here because this is the last layer in
        // the target that can still see the submitted value: a request body may state the member as
        // null even though the contract and the aggregate both declare it non-nullable, since that
        // declaration is not enforced during deserialisation. The terminal schema declares the column
        // NOT NULL, so the condition is an invariant violation rather than a handleable outcome, which
        // is why it is thrown for translation at the API edge instead of being given a reason code
        // this contract does not declare. The EMPTY string is deliberately still accepted: that is the
        // legacy "no text" value arriving explicitly, and it is stored as given.
        if (request.TabName is null)
        {
            throw new DomainException("UpdateTabRequest.TabName was null; the page name is required.")
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

        // The whole portal is needed for the ancestry test and for the renumbering pass that follows,
        // so it is read once here and reused. Pages in the recycle bin are included because they
        // still occupy positions in the legacy ordering, exactly as the legacy list did.
        IReadOnlyList<Tab> siblingSet = tab.PortalId is int owningPortalId
            ? await _tabs.GetByPortalIdAsync(owningPortalId, cancellationToken).ConfigureAwait(false)
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

            // MIGRATION: the legacy routine recorded every page's position in a hash table before it
            // spliced the working list, then persisted only the pages whose recorded values had
            // actually moved (L565 and L761-L769). That economy is preserved literally, and it is
            // recorded before the recomputation for the same reason the legacy code recorded it
            // before the splice. The edited page is excluded because it is staged unconditionally
            // below: its own metadata changed even when its position did not, and its parent has
            // already been reassigned, which would make a comparison against it meaningless.
            IReadOnlyDictionary<int, TabPosition> positions = SnapshotPositions(tree, tab.TabId);

            Portal? portal = await _portals.GetByIdAsync(portalId, includeAliases: false, cancellationToken)
                .ConfigureAwait(false);
            RecomputeTree(tree, portal?.AdminTabId);

            await StageMovedPagesAsync(tree, positions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A host-level page is not portal-scoped and the page repository exposes no host-wide
            // listing, so the ordering of the host tree is left untouched and only this page's own
            // depth and path are refreshed from its parent chain.
            Tab? parent = tab.ParentId is int hostParentId
                ? await _tabs.GetByIdAsync(hostParentId, cancellationToken).ConfigureAwait(false)
                : null;

            tab.Level = parent is null ? RootLevel : parent.Level + 1;
            tab.TabPath = await BuildHostTabPathAsync(tab, cancellationToken).ConfigureAwait(false);
        }

        // The edited page is staged through the wide update rather than the positional one, mirroring
        // the legacy pair exactly: the reordering routine reached the four-column positional
        // procedure for each page it moved, and its caller then reached the nineteen-column procedure
        // for the page actually being edited (L789). Staging is explicit rather than left to the
        // persistence layer's change tracker, because whether reads are tracked is an infrastructure
        // decision this layer must not depend on; both staging members are documented as no-ops for
        // an already-tracked page, so the explicit call costs nothing and removes the assumption.
        await _tabs.UpdateAsync(tab, cancellationToken).ConfigureAwait(false);

        // One commit for the whole tree. The renumbering pass touches every page of the portal, and
        // the legacy path issued those writes as independent statements with no enclosing
        // transaction, so a failure part-way through left the hierarchy renumbered inconsistently.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (tab.PortalId is int invalidatedPortalId)
        {
            // MIGRATION: the legacy cache clear for a page change was a PAIR of evictions, not one -
            // the portal's page collection and then the portal itself, the second carrying the
            // comment "Clear the Portal cache so the Pages count is correct" (L60-L63). Both are
            // reproduced: the portal projection carries a page count, so evicting only the page
            // collection would leave that count stale behind a successful update.
            _cache.InvalidateTabs(invalidatedPortalId);
            _cache.InvalidatePortal(invalidatedPortalId);
        }
        else
        {
            // MIGRATION: a host-level page has no portal, and the legacy code had no way to say so -
            // it passed the page's portal identifier straight through, which for a host page was the
            // integer sentinel -1, and so evicted the cache entries of the portal whose real
            // identifier is -1. That is a discovered cross-tenant defect; it is recorded here and
            // deliberately neither reproduced nor repaired. The installation-wide eviction used
            // instead is a strict superset of what a host page can have staled, so nothing survives
            // that should not.
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
        // use admits a deleted page. The repository returns recycled pages because the legacy read
        // did, so excluding them is this caller's policy and is applied here rather than asked of the
        // contract.
        IReadOnlyList<Tab> stored = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        List<Tab> tabs = stored.Where(candidate => !candidate.IsDeleted).ToList();

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
                next = await _tabs.GetByIdAsync(nextId, cancellationToken).ConfigureAwait(false);
            }

            current = next;
        }

        return false;
    }

    /// <summary>
    /// Records the position of every page in a portal's tree so that the pages the recomputation
    /// actually moves can be told apart from the pages it leaves where they were.
    /// </summary>
    /// <param name="tabs">Every page of the portal, as it stands before the recomputation.</param>
    /// <param name="excludedTabId">
    /// The page being edited, which is omitted because it is staged unconditionally and has already
    /// had its parent reassigned from the request.
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

    /// <summary>
    /// Stages a positional write for every page the recomputation moved, and for no other page.
    /// </summary>
    /// <param name="tabs">Every page of the portal, as it stands after the recomputation.</param>
    /// <param name="positions">The positions recorded before the recomputation.</param>
    /// <param name="cancellationToken">Token observed while the writes are staged.</param>
    /// <returns>A task that completes once every moved page has been staged.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the legacy routine's own economy, preserved. It compared each page's stored
    /// order, depth and parent against the values it had recorded and reached the positional
    /// procedure only when one of the three differed (L761-L769).
    /// </para>
    /// <para>
    /// MIGRATION: the materialised path is compared alongside those three, which the legacy
    /// comparison did not do - because the legacy code propagated a changed path through an entirely
    /// separate recursive pass over the edited page's children, rewriting each one whose path had
    /// changed and recursing (<c>UpdateChildTabPath</c>, L306-L320). Renaming a page moves its
    /// descendants' paths without moving their order, depth or parent, so comparing the path here is
    /// what lets one pass do the work of both legacy mechanisms. The positional write covers exactly
    /// these four columns, so the comparison and the write agree by construction.
    /// </para>
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

    /// <summary>
    /// A page's position in its portal's hierarchy: the four values the positional write persists.
    /// </summary>
    /// <param name="TabOrder">The page's sequence number among the portal's pages.</param>
    /// <param name="Level">The page's depth, where zero is the root.</param>
    /// <param name="ParentId">
    /// The page's parent, or <see langword="null"/> for a root-level page. Absence is nullable and
    /// never a number: this schema seeds page identities at zero and portal identities at minus one,
    /// so neither value can stand for "no parent".
    /// </param>
    /// <param name="TabPath">The page's materialised hierarchy path.</param>
    /// <remarks>
    /// MIGRATION: replaces the legacy routine's nested ordering helper class, which carried the first
    /// three of these values inside a hash table keyed by page identifier (L47-L51, L565). A record
    /// struct gives the value equality the comparison needs without a class allocation per page, and
    /// declaring it nested and private keeps it out of the layer's public surface, where it has no
    /// business being.
    /// </remarks>
    private readonly record struct TabPosition(int TabOrder, int Level, int? ParentId, string? TabPath);
}
