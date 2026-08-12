using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="Portal"/> tenant records through <see cref="DnnDbContext"/>.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the thirty portal stored procedures reached from the legacy core data
/// provider together with the data-access half of <c>Library/Components/Portal/PortalController.vb</c>.
/// The legacy reader loops that hydrated a <c>PortalInfo</c> column by column - and the reflection
/// hydrator in <c>Library/Components/Shared/CBO.vb</c> that did the same job generically - are both
/// replaced by the Entity Framework materialiser, so no hand-rolled fill method survives.
/// <para>
/// MIGRATION: caching is deliberately absent from every member below. The legacy controller
/// interleaved cache reads, an expiry computed as a per-entity timeout multiplied by a global
/// performance setting, and coarse portal-scoped and host-scoped invalidations directly with its data
/// access - <c>PortalController.vb</c> lines 208, 211, 218 and 240 for the read-and-populate, and
/// lines 916, 1128, 1131 and 1205 for the clears. None of that is reproduced here: no member consults
/// a cache, accepts a cache hint or evicts an entry. Caching and its invalidation are a separate
/// concern owned by the coordinated cache service, so a reader of this file can be certain that every
/// answer it returns came from the store on this call.
/// </para>
/// <para>
/// <b>The tracking rule, and why it is not uniform.</b> A read whose result may afterwards be
/// mutated and committed MUST stay tracked, because the application layer relies on the change
/// tracker rather than on an explicit re-staging call:
/// <c>PortalService.UpdatePortalAsync</c> reads through <see cref="GetByIdAsync"/>, applies the
/// request to the entity, and calls <see cref="IUnitOfWork.SaveChangesAsync"/> <i>without</i> calling
/// <see cref="UpdateAsync"/> at all. Detaching that read would not raise an error; it would silently
/// persist nothing, which is the worst failure mode available. <see cref="GetByIdAsync"/> and
/// <see cref="DeleteAsync"/> are therefore tracked by design, and <see cref="DeleteAsync"/> must be
/// so in any case because it removes the instance it loaded.
/// </para>
/// <para>
/// Every other read is genuinely read-only and applies <c>AsNoTracking</c>: the listings
/// (<see cref="ListAsync"/>, <see cref="GetAllAsync"/>), the tenant resolutions
/// (<see cref="GetByAliasAsync"/>, <see cref="GetByTabAsync"/>) and the projections
/// (<see cref="GetRoleNamesAsync"/>, <see cref="CountUsersForPortalsAsync"/>,
/// <see cref="CountPagesForPortalsAsync"/>). Tracking a whole page of entities that will only be
/// projected onto a data transfer object is pure overhead, and detaching them also removes the risk
/// that an unrelated commit in the same scope picks up an incidental edit to a listed row. A caller
/// that intends to modify a portal must obtain it by identifier through
/// <see cref="GetByIdAsync"/>; that is the single tracked entry point, which is what makes the rule
/// above checkable rather than a matter of habit.
/// </para>
/// <para>
/// The aggregate members - <see cref="ExistsAsync"/>, <see cref="TabBelongsToPortalAsync"/>,
/// <see cref="CountAsync"/>, <see cref="CountUsersAsync"/> and <see cref="CountPagesAsync"/> - carry
/// no <c>AsNoTracking</c> call and need none: a counting or existence terminal materialises no entity,
/// so the call would be inert. It is omitted rather than added for symmetry, because an inert call
/// invites a reader to believe it is doing something.
/// </para>
/// </remarks>
internal sealed class PortalRepository : IPortalRepository
{
    /// <summary>Ordering applied when the caller names no sortable property.</summary>
    /// <remarks>
    /// Chosen to match the legacy default rather than invented: the terminal paging procedure
    /// selected <c>ORDER BY PortalName</c>
    /// (<c>04.04.00.SqlDataProvider</c>, <c>GetPortalsByName</c>), so an unsorted request produces
    /// the sequence the legacy administration grid produced.
    /// </remarks>
    private const string DefaultSortProperty = "PortalName";

    /// <summary>
    /// Character that removes the special meaning of a <c>LIKE</c> metacharacter in the name pattern
    /// this repository builds.
    /// </summary>
    /// <remarks>
    /// Declared once and passed explicitly to the <c>LIKE</c> it is used with, because SQL Server has
    /// no default escape character: without an <c>ESCAPE</c> clause a backslash in a pattern is an
    /// ordinary literal, so the escaping performed by <see cref="LikePrefixPattern(string)"/> would
    /// silently do nothing and a caller's own metacharacters would go on acting as pattern syntax.
    /// </remarks>
    private const string LikeEscapeCharacter = "\\";

    /// <summary>
    /// The page tally reported for a portal that does not exist or records no administration page.
    /// </summary>
    /// <remarks>
    /// MIGRATION: minus one is what the terminal <c>GetTabCount</c> returned in both of those cases and
    /// is therefore preserved rather than smoothed to zero. The procedure read <c>@AdminTabId</c> into a
    /// variable and then compared every row against it; a null made each row's predicate UNKNOWN, so
    /// <c>COUNT(*)</c> was nought and <c>COUNT(*) - 1</c> was minus one. It is NOT the legacy
    /// <c>Null.NullInteger</c> sentinel and must not be read as one - it is an arithmetic consequence,
    /// and a caller that wanted "unknown" would have had no way to tell the two apart, which is exactly
    /// why the value is carried through unchanged rather than reinterpreted here.
    /// </remarks>
    private const int NoAdministrationPageTally = -1;

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="PortalRepository"/> class.</summary>
    /// <param name="dbContext">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The context is the only collaborator. No cache, clock, logger, request accessor, password
    /// hasher, file system or service locator is injected, so nothing this type does can depend on
    /// ambient state - which is precisely what the reflection-resolved
    /// <c>DataProvider.Instance()</c> singleton it replaces could not promise.
    /// </remarks>
    public PortalRepository(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    /// <inheritdoc />
    // MIGRATION: this single member retires three separate legacy contracts. `GetPortalsByName`
    // (DataProvider.vb:L102) returned a forward-only reader for one page; `GetPortals`
    // (PortalController.vb:L1263) returned an untyped, elementless ArrayList built by
    // FillPortalInfoCollection; and `GetPortalCount` (DataProvider.vb:L100) existed only because
    // neither reader could report a grand total, so a caller wanting "this page, and how many
    // altogether?" had to make two round trips. PagedResult<Portal> carries the typed records and the
    // total together, so the second read and the untyped collection both disappear.
    //
    // MIGRATION: the page base is ZERO, and that is the legacy base rather than a modern preference.
    // The terminal procedure computed its offset as `SET @PageLowerBound = @PageSize * @PageIndex`
    // (04.04.00.SqlDataProvider, GetPortalsByName), which is exactly what Paging.SkipCount computes
    // and exactly the base PagedResult<T> documents. No sentinel is involved anywhere: the legacy
    // "give me everything" call shape passed the -1 of Null.vb:L41 as index, size and total alike,
    // whereas an unpaged request here is a page size of zero routed through the named Unpaged
    // factory. -1 is never passed as a coordinate, which matters twice over because -1 is also a real
    // PortalID in this schema.
    public async Task<PagedResult<Portal>> ListAsync(
        int pageIndex,
        int pageSize,
        string? nameFilter,
        string? sortBy,
        bool descending,
        CancellationToken cancellationToken = default)
    {
        // Read-only: the two production call sites project the page onto data transfer objects and
        // read its total, and neither mutates a listed entity. See the tracking rule on the type.
        IQueryable<Portal> query = _dbContext.Portals.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            // MIGRATION: the legacy grid matched a PREFIX of the site name, and this member matches a
            // prefix. The pattern was assembled at the call site rather than in the procedure:
            // Website/admin/Portal/Portals.ascx.vb line 142 reads
            // `GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)` - a single
            // TRAILING wildcard and no leading one - and the surviving procedure applies it unchanged
            // with `WHERE PortalName LIKE @NameToMatch`
            // (Website/Providers/DataProviders/SqlDataProvider/04.04.00.SqlDataProvider lines 245-269).
            // `LIKE 'j%'` is a prefix test, so a mid-string fragment matched nothing in the legacy grid.
            //
            // ⚠ DO NOT WIDEN THIS TO A CONTAINMENT TEST. An earlier revision used Contains, reasoning
            // that a widening loses no legacy result. It loses something else: the FILTER STRIP'S
            // MEANING. That strip is an A-to-Z index - twenty-six single letters read from
            // `Filter.Text`, named "Filter portals by first letter" - and against a containment test
            // pressing "A" returns every title with an "a" anywhere in it, which on a real
            // installation is very nearly all of them. Runtime testing measured exactly that. The
            // strip and the free-text box share one filter parameter, so the predicate cannot be
            // prefix for one and containment for the other; prefix is the one the legacy screen had
            // and the one the strip's own label promises. Rule T5 (identical inputs, identical
            // outcomes) settles it: equivalence is achievable here, so it is achieved rather than
            // documented away. The account listing's letter filter already matches by prefix, so both
            // listings now answer the same way.
            //
            // The WILDCARD HARDENING is kept, and it is a separate concern from the predicate's shape.
            // The legacy pattern was string concatenation, so a caller's own `%` or `_` acted as a
            // pattern and a single per cent sign matched every portal in the installation. Every
            // metacharacter in the caller's text is escaped here instead, so those characters match
            // themselves and the only live wildcard is the one this repository appends.
            //
            // ⚠ THE COLUMN IS LEFT UNWRAPPED, AND FOLDING ITS CASE HERE WAS BOTH SLOWER AND LESS
            // FAITHFUL. The predicate used to read `p.PortalName.ToLower().StartsWith(wanted)`, which
            // emits `LOWER([PortalName]) LIKE @p`. A function applied to the column makes the
            // predicate NON-SARGABLE: the server can no longer seek on `PortalName`, so every filtered
            // read - and the COUNT that shares this query - degrades to a full scan of the tenant
            // table, and no index a deployment adds can ever be used. An unwrapped column with a
            // prefix pattern is the one shape SQL Server can seek.
            //
            // Faithfulness points the same way, which is what settles it. The terminal legacy
            // procedure filtered with a bare `WHERE PortalName LIKE @NameToMatch`
            // (`04.04.00.SqlDataProvider`, `GetPortalsByName`), so the case-insensitivity the legacy
            // grid exhibited came from the DATABASE COLLATION and from nothing else. Folding both
            // sides in the application therefore did not preserve legacy behaviour - it replaced a
            // collation-governed comparison with an invariant-culture one, which answers differently
            // on a case-SENSITIVE installation: the legacy screen would match nothing there, and the
            // folded predicate matches everything with the right letters. Deferring to the collation
            // reproduces whichever answer the installation actually gave. Rule T5.
            //
            // The integration suite asserts case-insensitive prefix matching, and it passes because
            // the test database carries a case-insensitive collation - which is the same reason the
            // legacy grid behaved that way, now asserted through the same mechanism rather than
            // around it.
            string pattern = LikePrefixPattern(nameFilter.Trim());
            query = query.Where(p => EF.Functions.Like(p.PortalName, pattern, LikeEscapeCharacter));
        }

        query = ApplyOrder(query, sortBy, descending);

        if (pageSize == 0)
        {
            // A page size of zero requests every match, which is how the callers that need a
            // complete tenant list ask for one without inventing a sentinel page size. It is routed
            // through Unpaged rather than Create because Create rejects a zero page size paired with
            // any non-zero index, and because the total of an unpaged read is the record count
            // itself - so no separate counting round trip is issued for this branch.
            List<Portal> all = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return PagedResult<Portal>.Unpaged(all);
        }

        // Counted before the page is read, and counted over the same filtered query, so the total
        // describes the same set the page was drawn from. The token is threaded through both reads.
        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<Portal> rows = await query
            .Skip(Paging.SkipCount(pageIndex, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<Portal>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    // MIGRATION: replaces `GetPortals` (DataProvider.vb:L101) and the ArrayList its controller
    // counterpart returned (PortalController.vb:L1263). Requesting every tenant is a NAMED member
    // here; the legacy convention of asking for it by passing the -1 null-integer sentinel as a page
    // coordinate is not reproduced, and could not be, because -1 is itself a real PortalID.
    public async Task<IReadOnlyList<Portal>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Ordered for the same reason the paged read is ordered: a caller that walks the whole
        // installation must see a stable sequence between calls. The primary key terminates the
        // order so tenants sharing a name still have a defined relative position. Materialised
        // before returning, so no deferred query escapes into a caller whose scope may outlive the
        // context that built it.
        return await _dbContext.Portals
            .AsNoTracking()
            .OrderBy(p => p.PortalName)
            .ThenBy(p => p.PortalId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    // MIGRATION: replaces `GetPortal` (DataProvider.vb:L97), whose reader the controller hydrated
    // column by column. This is the ONLY tracked read on the type, and deliberately so: it is the
    // entry point the application layer uses before a write, and PortalService.UpdatePortalAsync
    // commits the entity it receives here through IUnitOfWork WITHOUT re-staging it. Adding
    // AsNoTracking would therefore turn every portal update into a silent no-op rather than an error.
    public async Task<Portal?> GetByIdAsync(int portalId, bool includeAliases = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Portal> query = _dbContext.Portals;

        if (includeAliases)
        {
            // Loaded only on request. The aliases are needed by the detail projection and by the
            // update path that rewrites them, and by nothing else, so a listing does not pay for
            // them. No other navigation is pulled in: a portal owns modules, pages, roles,
            // memberships and module grants, and including them indiscriminately would turn one
            // lookup into a multi-table fan-out for callers that asked for none of it.
            query = query.Include(p => p.PortalAliases);
        }

        // PortalID is IDENTITY(-1, 1), so both -1 and 0 are legitimate keys and neither may be
        // treated as "absent" - the shipped default portal really is portal 0 and the first generated
        // tenant really is -1, while -1 is simultaneously the legacy Null.NullInteger marker
        // (Null.vb:L41). The identifier is therefore matched exactly, with no sign test, no range
        // test and no sentinel shortcut. Absence is reported as null and never as a numeric value.
        return await query
            .FirstOrDefaultAsync(p => p.PortalId == portalId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Portal?> GetByAliasAsync(string httpAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = httpAlias.Trim().ToLowerInvariant();

        if (wanted.Length is 0)
        {
            return null;
        }

        // MIGRATION: the historical tenant-resolution procedure `GetPortalSettings` matched the alias
        // with a leading-and-trailing wildcard - `where PortalAlias like '%' + @PortalAlias + '%'`
        // (01.00.00.SqlDataProvider:L4569 onward) - and then took `min(PortalID)` of whatever matched.
        // One tenant's alias being a SUBSTRING of another's could therefore resolve a request to the
        // wrong tenant, and the lowest-identifier tie-break silently decided which. That procedure was
        // dropped outright at 02.02.00.SqlDataProvider:L267 and the terminal alias comparison in this
        // schema is an equality test - see `GetPortalByTab` at 02.02.02.SqlDataProvider:L3942, whose
        // predicate is `HTTPAlias = @HTTPAlias`. The comparison below is that equality test: a WHOLE
        // VALUE match with no pattern, no wildcard, no containment and no concatenation, so an alias
        // cannot resolve a tenant it merely occurs inside. The correction is recorded in the migration
        // notes.
        //
        // Case is folded on both sides because the contract on IPortalRepository REQUIRES a
        // case-insensitive comparison, and because the sibling PortalAliasRepository folds the same
        // column the same way; a bare comparison would delegate that requirement to whatever collation
        // the installation happens to carry and would be case-SENSITIVE on a provider that compares
        // ordinally. Folding is not a widening - the operator either side of it is still equality.
        //
        // The ordering is determinism insurance rather than tie-breaking: HTTPAlias carries a UNIQUE
        // index (IX_PortalAlias), so at most one alias and therefore at most one tenant can match, and
        // the legacy min(PortalID) choice has nothing left to choose between.
        return await _dbContext.Portals
            .AsNoTracking()
            .Where(p => p.PortalAliases.Any(a => a.HttpAlias != null && a.HttpAlias.ToLower() == wanted))
            .OrderBy(p => p.PortalId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Portal?> GetByTabAsync(int tabId, string httpAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = httpAlias.Trim().ToLowerInvariant();

        if (wanted.Length is 0)
        {
            return null;
        }

        // MIGRATION: this is the terminal `GetPortalByTab` shape (02.02.02.SqlDataProvider:L3942),
        // reproduced relationally rather than literally. That procedure read
        //     select HTTPAlias from PortalAlias
        //     inner join Tabs on PortalAlias.PortalId = Tabs.PortalId
        //     where TabId = @TabId and HTTPAlias = @HTTPAlias
        // so the join condition IS the tenant-isolation check: the alias row and the page row had to
        // share a PortalId. Requiring both predicates of the SAME portal below expresses exactly that,
        // and returns the portal itself rather than echoing back the alias the caller already had.
        // Both halves are required as a unit - that is what stops a page identifier belonging to one
        // tenant from being read under another tenant's alias. The alias comparison is the same
        // whole-value equality used for alias resolution, never a substring or pattern, and there is
        // deliberately no host or global fallback: the terminal procedure had none, so inventing one
        // would widen tenant resolution beyond the behaviour being preserved.
        return await _dbContext.Portals
            .AsNoTracking()
            .Where(p => p.PortalAliases.Any(a => a.HttpAlias != null && a.HttpAlias.ToLower() == wanted)
                && p.Tabs.Any(t => t.TabId == tabId))
            .OrderBy(p => p.PortalId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    // MIGRATION: replaces `VerifyPortal` (DataProvider.vb:L107), which reported existence by handing
    // back a reader the caller had to test for a row. Existence is a boolean, so it is answered as one
    // and no record is materialised. The identifier is matched exactly: -1 and 0 are real keys here, so
    // there is no sign or range test to apply. An existence terminal tracks nothing, so no
    // AsNoTracking call is needed and none is added.
    public Task<bool> ExistsAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Portals.AnyAsync(p => p.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    // MIGRATION: replaces `VerifyPortalTab` (DataProvider.vb:L106), which likewise reported a boolean
    // fact through a reader.
    public Task<bool> TabBelongsToPortalAsync(int portalId, int tabId, CancellationToken cancellationToken = default)
    {
        // Asked of the page table rather than through the portal's navigation, so the answer is
        // one existence probe. A page that does not exist and a page belonging to another tenant
        // are both false, which is exactly what the legacy reader-returns-no-row result meant.
        // Tab.PortalId is nullable because a host page belongs to no tenant; a null never equals a
        // supplied identifier, so a host page is excluded without a special case.
        return _dbContext.Tabs.AnyAsync(t => t.TabId == tabId && t.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    // MIGRATION: replaces `GetPortalCount` (DataProvider.vb:L100). The legacy surface needed a
    // standalone count because its paged reader could not report a grand total; within a page that
    // need is now met by PagedResult<Portal>, and this member survives only for the callers that want
    // the installation-wide tally on its own.
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.Portals.CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountUsersAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Membership of a tenant is the UserPortals row, not a column on Users, so the count is
        // taken there. Unauthorised members are included because the legacy portals grid counted
        // every registered account against its tenant regardless of authorisation state.
        return _dbContext.UserPortals.CountAsync(m => m.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: this reproduces the terminal <c>GetTabCount</c>
    /// (<c>04.04.00.SqlDataProvider</c> lines 511-527) exactly, which is the procedure the legacy portal
    /// grid actually displayed: <c>PortalInfo.Pages</c> (<c>PortalInfo.vb</c> lines 320-325) resolved its
    /// value through <c>TabController.GetTabCount(PortalID)</c>. Three parts of that predicate are
    /// counter-intuitive and every one of them is deliberate - see
    /// <see cref="IPortalRepository.CountPagesAsync"/>, where the reasoning is recorded once. An earlier
    /// revision counted every non-deleted page instead, which agreed with the legacy figure on no portal
    /// at all.
    /// </remarks>
    public Task<int> CountPagesAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return CountPagesLikeGetTabCountAsync(portalId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> CountUsersForPortalsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalIds);

        int[] wanted = portalIds.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return new Dictionary<int, int>();
        }

        // One grouped read replaces one read per identifier. The predicate is the same one the
        // single-portal member uses - every UserPortals row counts, authorised or not - so the batched
        // and unbatched tallies cannot disagree. The containment test is over the caller's identifier
        // SET, which translates to an IN list; it is not a text search and has nothing to do with the
        // substring matching this repository refuses on alias columns.
        List<PortalTally> tallies = await _dbContext.UserPortals
            .AsNoTracking()
            .Where(m => wanted.Contains(m.PortalId))
            .GroupBy(m => m.PortalId)
            .Select(group => new PortalTally { PortalId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Densify(wanted, tallies);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The batched counterpart of <see cref="CountPagesAsync"/>, and it answers the identical question:
    /// the terminal <c>GetTabCount</c>. Two reads serve the whole set - one projection of each requested
    /// portal's administration page, one grouped page tally - and the per-portal arithmetic is then done
    /// in memory, so a listing costs a fixed number of round trips rather than two per row.
    /// </para>
    /// <para>
    /// MIGRATION: the two members are made to agree by CONSTRUCTION rather than by intent. The predicate
    /// that excludes the administration page and its direct children is stated once, in
    /// <see cref="ExcludesAdministrationPage"/>, and both this member and the single-portal one apply it,
    /// so neither can drift into answering a different question. An earlier revision had them agreeing
    /// with each other and with neither <c>GetTabCount</c>, which is the failure mode a
    /// batched-versus-single comparison alone cannot detect.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, int>> CountPagesForPortalsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalIds);

        int[] wanted = portalIds.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return new Dictionary<int, int>();
        }

        // The administration page of each requested portal, in one read. A portal that is absent from
        // this map either does not exist or records no administration page; the legacy statement could
        // not distinguish those two either, and both answer minus one below.
        Dictionary<int, int> administrationPages = await _dbContext.Portals
            .AsNoTracking()
            .Where(portal => wanted.Contains(portal.PortalId) && portal.AdminTabId != null)
            .Select(portal => new PortalTally { PortalId = portal.PortalId, Count = portal.AdminTabId!.Value })
            .ToDictionaryAsync(row => row.PortalId, row => row.Count, cancellationToken)
            .ConfigureAwait(false);

        // Every page of every requested portal, WITH its parent, because the exclusion is expressed over
        // both the page's own key and its parent's. Tab.PortalId is nullable because a host page belongs
        // to no tenant, so the identifier is tested for a value before it is matched; a null can never
        // equal a supplied identifier and a host page is therefore excluded, exactly as the
        // single-portal member excludes it.
        //
        // MIGRATION: the soft-delete predicate is deliberately ABSENT, on both members. GetTabCount
        // states no IsDeleted condition, so a page awaiting emptying of the recycle bin was counted by
        // the legacy grid, and Rule T5 preserves that rather than improving on it.
        List<PageParentage> pages = await _dbContext.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId.HasValue && wanted.Contains(tab.PortalId.Value))
            .Select(tab => new PageParentage
            {
                PortalId = tab.PortalId!.Value,
                TabId = tab.TabId,
                ParentId = tab.ParentId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var tallies = new List<PortalTally>(wanted.Length);

        foreach (int portalId in wanted)
        {
            if (!administrationPages.TryGetValue(portalId, out int administrationTabId))
            {
                tallies.Add(new PortalTally { PortalId = portalId, Count = NoAdministrationPageTally });
                continue;
            }

            int counted = pages.Count(page => page.PortalId == portalId
                && ExcludesAdministrationPage(page.TabId, page.ParentId, administrationTabId));

            tallies.Add(new PortalTally { PortalId = portalId, Count = counted - 1 });
        }

        // Densify is still applied, so the contract's totality holds even though every identifier has
        // already been given a row above: it is the one place that promise is expressed.
        return Densify(wanted, tallies);
    }

    /// <summary>
    /// Counts one portal's pages the way the terminal <c>GetTabCount</c> counted them.
    /// </summary>
    /// <param name="portalId">The portal to tally.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>
    /// The legacy page tally, which is minus one for a portal that does not exist or records no
    /// administration page.
    /// </returns>
    /// <remarks>
    /// A faithful translation of <c>04.04.00.SqlDataProvider</c> lines 511-527, in its own order: read
    /// <c>AdminTabId</c> from the portal, then <c>SELECT COUNT(*) - 1</c> over the portal's pages
    /// excluding the administration page and its direct children. It is the same implementation
    /// <c>TabRepository.CountByPortalIdAsync</c> carries, because the two answer the same legacy
    /// question and the schema offers no shared place for it below the repository layer.
    /// </remarks>
    private async Task<int> CountPagesLikeGetTabCountAsync(int portalId, CancellationToken cancellationToken)
    {
        int? adminTabId = await _dbContext.Portals
            .AsNoTracking()
            .Where(portal => portal.PortalId == portalId)
            .Select(portal => portal.AdminTabId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (adminTabId is not int administrationTabId)
        {
            // Reached both when the portal does not exist and when it records no administration page.
            // The legacy statement could not distinguish them either: comparing against a null
            // @AdminTabId made every row's predicate UNKNOWN, so COUNT(*) was 0 and the expression
            // returned 0 - 1.
            return NoAdministrationPageTally;
        }

        int counted = await _dbContext.Tabs
            .AsNoTracking()
            .CountAsync(
                tab => tab.PortalId == portalId
                    && tab.TabId != administrationTabId
                    // The null disjunct is spelled out rather than left to the provider's null
                    // semantics, because it is the legacy predicate's own "OR ParentId IS NULL" branch
                    // and it is what keeps every root page inside the count.
                    && (tab.ParentId == null || tab.ParentId != administrationTabId),
                cancellationToken)
            .ConfigureAwait(false);

        return counted - 1;
    }

    /// <summary>
    /// Applies the legacy exclusion of the administration page and its direct children to one page.
    /// </summary>
    /// <param name="tabId">The page's own key.</param>
    /// <param name="parentId">The page's parent, or <see langword="null"/> for a root page.</param>
    /// <param name="administrationTabId">The portal's administration page.</param>
    /// <returns><see langword="true"/> when the page counts towards the tally.</returns>
    /// <remarks>
    /// The in-memory counterpart of the predicate <see cref="CountPagesLikeGetTabCountAsync"/> issues to
    /// the store, stated once so the batched tally cannot drift from the single one. Only DIRECT children
    /// of the administration page are excluded: the legacy predicate tested <c>ParentId</c> and nothing
    /// deeper, so a grandchild of the administration page was counted, and that is preserved rather than
    /// corrected.
    /// </remarks>
    private static bool ExcludesAdministrationPage(int tabId, int? parentId, int administrationTabId) =>
        tabId != administrationTabId && (parentId is null || parentId.Value != administrationTabId);

    /// <summary>
    /// Expands a grouped tally into a total map over the identifiers that were asked for.
    /// </summary>
    /// <remarks>
    /// A grouped read returns no row for an identifier with nothing to count, so the projection is
    /// sparse. The batched contract promises a TOTAL map, which is what lets a caller index it
    /// directly instead of remembering that an absent key means zero, so the zeros are filled in
    /// here rather than at every call site.
    /// </remarks>
    /// <param name="wanted">The distinct identifiers the caller asked about.</param>
    /// <param name="tallies">The rows the grouped read produced, at most one per identifier.</param>
    /// <returns>One entry per wanted identifier.</returns>
    private static Dictionary<int, int> Densify(int[] wanted, List<PortalTally> tallies)
    {
        var counts = new Dictionary<int, int>(wanted.Length);

        foreach (int portalId in wanted)
        {
            counts[portalId] = 0;
        }

        foreach (PortalTally tally in tallies)
        {
            counts[tally.PortalId] = tally.Count;
        }

        return counts;
    }

    /// <summary>
    /// Carries one grouped tally row out of a batched count.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one because the projection is consumed by a shared
    /// helper, and an anonymous type cannot be named in that helper's signature.
    /// </remarks>
    private sealed class PortalTally
    {
        /// <summary>Gets or sets the portal the tally belongs to.</summary>
        public int PortalId { get; set; }

        /// <summary>Gets or sets the number of rows counted for that portal.</summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// Carries one page's own key and its parent out of a batched page read.
    /// </summary>
    /// <remarks>
    /// The batched page tally needs both keys, because the legacy exclusion is expressed over the page's
    /// own identifier AND its parent's. A named type rather than an anonymous one for the same reason
    /// <see cref="PortalTally"/> is named: the projection is consumed by a shared predicate whose
    /// signature has to name it.
    /// </remarks>
    private sealed class PageParentage
    {
        /// <summary>Gets or sets the portal the page belongs to.</summary>
        public int PortalId { get; set; }

        /// <summary>Gets or sets the page's own key. <c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>, so zero is genuine.</summary>
        public int TabId { get; set; }

        /// <summary>Gets or sets the page's parent, or <see langword="null"/> for a root page.</summary>
        public int? ParentId { get; set; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> GetRoleNamesAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Projected rather than materialised: only the two nominated role identifiers are wanted, so
        // the whole tenant row is not read to obtain them.
        var assignments = await _dbContext.Portals
            .AsNoTracking()
            .Where(p => p.PortalId == portalId)
            .Select(p => new { p.AdministratorRoleId, p.RegisteredRoleId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (assignments is null)
        {
            return new Dictionary<int, string>();
        }

        List<int> wanted = new(2);

        if (assignments.AdministratorRoleId.HasValue)
        {
            wanted.Add(assignments.AdministratorRoleId.Value);
        }

        if (assignments.RegisteredRoleId.HasValue && assignments.RegisteredRoleId != assignments.AdministratorRoleId)
        {
            wanted.Add(assignments.RegisteredRoleId.Value);
        }

        if (wanted.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        // RoleID is IDENTITY(0, 1), so zero is a legitimate role key. Only roles that actually
        // exist are returned, which is what lets a caller distinguish an unset assignment from one
        // that points at a role somebody has since deleted.
        List<KeyValuePair<int, string>> rows = await _dbContext.Roles
            .AsNoTracking()
            .Where(r => wanted.Contains(r.RoleId))
            .Select(r => new KeyValuePair<int, string>(r.RoleId, r.RoleName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, string> names = new(rows.Count);

        foreach (KeyValuePair<int, string> row in rows)
        {
            names[row.Key] = row.Value;
        }

        return names;
    }

    /// <inheritdoc />
    // MIGRATION: collapses two legacy insert paths onto one entity. `AddPortalInfo`
    // (DataProvider.vb:L93) took FOURTEEN positional arguments and wrote two aggregates - it created
    // the portal's administrator from the name, surname, username, password and address handed to it -
    // while `CreatePortal` (L94) took NINE and wrote the Portals row alone. Composing two aggregates
    // in one call is not reproduced: the application service stages the portal here, stages the
    // administrator through the user contract, and commits both together.
    //
    // MIGRATION: the hosting charge is staged from the entity's `decimal HostFee`, and the schema is
    // why. Both legacy declarations typed it `As Double` (DataProvider.vb:L93, L94, L104 and
    // SqlDataProvider.vb:L598, L601, L631), but the terminal column is `money` - the procedure
    // parameter is declared `@HostFee money` at 02.02.02.SqlDataProvider - and binary floating point
    // cannot represent a decimal currency value exactly. The entity therefore carries `decimal`, the
    // configuration maps it to `money`, and no `double` appears anywhere on this path.
    public Task AddAsync(Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: staged, not written, and no identifier is returned. Each legacy add member ended
        // by reading back the scope identity and returning the generated key, which forced every
        // insert to be durable on its own. Returning a key here would require flushing inside this
        // member and would split the multi-table tenant creation at PortalController.vb:L980 back into
        // independently durable statements - the precise defect that left a half-created portal
        // unrecoverable. The key appears on Portal.PortalId once IUnitOfWork.SaveChangesAsync returns.
        //
        // MIGRATION: provisioning is NOT performed here. The legacy CreatePortal at
        // PortalController.vb:L980 took fifteen positional arguments and went on to create the
        // administrator account, the host alias, the stock roles, the pages, the module instances, the
        // home directory through a FolderController and the profile definitions, then parsed a portal
        // template. All of that is application orchestration across several aggregates committed as one
        // unit of work; this member stages exactly one Portals row and nothing else.
        _dbContext.Portals.Add(portal);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    // MIGRATION: one member replaces two procedures that wrote the SAME Portals row from opposite
    // ends - `UpdatePortalInfo` (DataProvider.vb:L104) with TWENTY-SEVEN positional arguments covering
    // the descriptive and configuration columns, and `UpdatePortalSetup` (L105) with NINE covering the
    // administrator and the well-known page assignments. Splitting one row across two positional lists
    // made every caller responsible for supplying every column in the right order, and made a partial
    // update indistinguishable from an intentional overwrite with defaults. The entity carries its own
    // modified state instead, so a caller reads a portal, changes what it means to change, and stages
    // the result; nothing is durable until the unit of work commits.
    public Task UpdateAsync(Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        cancellationToken.ThrowIfCancellationRequested();

        // A portal read through GetByIdAsync is already tracked, so its modifications are staged by
        // the tracker and this call is the caller's explicit statement of intent rather than the
        // mechanism. An untracked instance - one rebuilt outside this context, or read through one of
        // the detached read members - is attached and marked modified so the same call works for it
        // too, which keeps the contract honest for a caller that did not obtain the entity from the
        // tracked entry point. Nothing is written either way.
        //
        // MIGRATION: the detached branch ASSIGNS THE STATE and must never call DbSet.Update, and that
        // is a correctness requirement of this schema rather than a stylistic preference. DbSet.Update
        // chooses between Added and Modified by asking whether the key "is set", and it reads an int
        // key of 0 as unset. dbo.Portals.PortalID is declared IDENTITY(-1, 1)
        // (01.00.00.SqlDataProvider:L77, re-declared by the Tmp_Portals rebuild at
        // 01.00.05.SqlDataProvider:L1366, and recorded in Schema/TerminalSchema.manifest), so the
        // FIRST TWO tenants of an installation bear -1 and 0 - which means Update(portal) on a detached
        // portal 0 staged an INSERT, silently duplicated the tenant under a freshly generated key and
        // left the addressed row exactly as it was, while still reporting success. Assigning
        // EntityState.Modified attaches the instance and marks its scalar properties modified without
        // consulting the key at all, and without walking the navigation graph - which a portal, whose
        // aliases, roles, pages and modules all hang off it, is the worst possible entity to have
        // walked. This is the same -1/0 sentinel collision the migration analysis records, and it is
        // shared by dbo.Tabs, dbo.Roles, dbo.RoleGroups and dbo.Modules: here 0 and -1 are values,
        // never absences.
        EntityEntry<Portal> entry = _dbContext.Entry(portal);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // MIGRATION: replaces `DeletePortalInfo` (DataProvider.vb:L95), which likewise identified its
        // target by identifier alone. The row is resolved before removal because that is what the
        // contract's shape requires, and the read is deliberately TRACKED - an entity must be tracked
        // to be staged for removal, so AsNoTracking has no place on this path. The identifier is
        // matched exactly, so a valid -1 or 0 is deleted like any other key. Dependent rows are left
        // to the schema's own cascade rules rather than a deletion order encoded here; sequencing
        // dependent writes is not a decision a persistence contract should make.
        Portal? portal = await _dbContext.Portals
            .FirstOrDefaultAsync(p => p.PortalId == portalId, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null)
        {
            // Nothing to stage, and that is not an error. A caller that has already established
            // absence need not distinguish the two cases, so the call is idempotent.
            return;
        }

        _dbContext.Portals.Remove(portal);
    }

    /// <summary>
    /// Applies a deterministic ordering to a tenant query.
    /// </summary>
    /// <param name="query">The query to order.</param>
    /// <param name="sortBy">The sortable property the caller named, or <see langword="null"/>.</param>
    /// <param name="descending">Whether the named property is applied in descending order.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// An ordering is applied unconditionally, including when the caller names nothing and when the
    /// caller names something this repository does not recognise. Skip-and-take paging over an
    /// unordered relational query has no defined row assignment, so a page could otherwise repeat
    /// or omit rows between requests. Every ordering ends on the primary key so that rows sharing a
    /// sort value still have a stable relative order.
    /// </remarks>
    // MIGRATION: the arms below are exactly the five names the boundary admits for this collection,
    // and that correspondence is the point rather than a coincidence. The permitted set is declared in
    // Application/Validation/SortableFields.cs as Portals, the sealed PortalPagedRequestValidator
    // validates against that set alone, and every one of its members is a column of the projected list
    // item - so a name a caller may send is a name this method acts on, and there is no third category
    // of "accepted but ignored".
    //
    // Two earlier arms were removed rather than kept as harmless extras: DESCRIPTION and CURRENCY. Both
    // named real Portal columns, but neither appeared in the permitted set and neither is projected onto
    // PortalListItemDto, so the boundary refused the names before the query was ever built and the arms
    // could not be reached. Leaving unreachable arms in place is not neutral - it invites a later reader
    // to conclude the collection sorts by a field the contract does not offer, which is precisely the
    // mismatch this method now exists to prevent. If either field is ever wanted, it must be added to
    // the projection, to the permitted set and to this switch together.
    //
    // The default arm still catches an unrecognised name, and that is defence in depth rather than
    // tolerance: the boundary refuses such a name with a field error long before this runs, but a caller
    // reaching the repository from another entry point must still receive a deterministically ordered
    // page rather than an unordered one.
    private static IQueryable<Portal> ApplyOrder(IQueryable<Portal> query, string? sortBy, bool descending)
    {
        string property = string.IsNullOrWhiteSpace(sortBy) ? DefaultSortProperty : sortBy.Trim();

        return property.ToUpperInvariant() switch
        {
            "PORTALID" => descending
                ? query.OrderByDescending(p => p.PortalId)
                : query.OrderBy(p => p.PortalId),
            "EXPIRYDATE" => descending
                ? query.OrderByDescending(p => p.ExpiryDate).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.ExpiryDate).ThenBy(p => p.PortalId),
            "HOSTFEE" => descending
                ? query.OrderByDescending(p => p.HostFee).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.HostFee).ThenBy(p => p.PortalId),
            "HOSTSPACE" => descending
                ? query.OrderByDescending(p => p.HostSpace).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.HostSpace).ThenBy(p => p.PortalId),
            _ => descending
                ? query.OrderByDescending(p => p.PortalName).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.PortalName).ThenBy(p => p.PortalId),
        };
    }
    /// <summary>
    /// Turns literal filter text into a <c>LIKE</c> pattern that matches it as a prefix.
    /// </summary>
    /// <param name="text">The literal text to match at the start of a tenant name.</param>
    /// <returns>
    /// A pattern for use with <see cref="LikeEscapeCharacter"/> as the escape character, matching any
    /// name that begins with <paramref name="text"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every metacharacter in the caller's text is escaped, so the match is a literal prefix rather than
    /// a pattern the caller can influence. Only the appended trailing wildcard is left live, which is
    /// the shape the legacy filter had: the terminal <c>GetPortalsByName</c> received its argument with a
    /// single trailing wildcard already attached and applied no escaping of its own, so a per cent sign
    /// typed into the filter box acted as a wildcard and matched every tenant in the installation.
    /// </para>
    /// <para>
    /// The escape character is replaced FIRST, and the order matters rather than being incidental. Were
    /// it replaced after the others, the backslash this method had just introduced in front of a
    /// metacharacter would itself be escaped, leaving a literal backslash followed by a still-live
    /// metacharacter - so escaping would produce precisely the pattern it was meant to prevent.
    /// </para>
    /// <para>
    /// Three metacharacters are escaped and a fourth deliberately is not. <c>%</c> and <c>_</c> are the
    /// SQL wildcards; <c>[</c> opens a character-class range. A closing <c>]</c> needs no escape because
    /// it has no meaning unless a range was opened, and escaping the opener is what guarantees none was.
    /// </para>
    /// <para>
    /// Deliberately identical to the account repository's helper of the same name, down to the escape
    /// order and the escaped set. The two listings share a filter affordance and must answer a typed
    /// metacharacter the same way; a shared home for it would have to sit in the persistence layer's
    /// public surface, which is a wider commitment than two private helpers of eight lines.
    /// </para>
    /// </remarks>
    private static string LikePrefixPattern(string text)
    {
        string literal = text
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal)
            .Replace("[", LikeEscapeCharacter + "[", StringComparison.Ordinal);

        return literal + "%";
    }
}
