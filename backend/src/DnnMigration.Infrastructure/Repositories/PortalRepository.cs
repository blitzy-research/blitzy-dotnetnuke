using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>Reads and writes <see cref="Portal"/> tenant records through <see cref="DnnDbContext"/>.</summary>
/// <remarks>
/// <para>
/// <b>The tracking rule, and why it is not uniform.</b> A read whose result may afterwards be mutated and
/// committed MUST stay tracked, because the application layer relies on the change tracker rather than on
/// an explicit re-staging call: <c>PortalService.UpdatePortalAsync</c> reads through <see
/// cref="GetByIdAsync"/>, applies the request to the entity, and calls <see
/// cref="IUnitOfWork.SaveChangesAsync"/> <i>without</i> calling <see cref="UpdateAsync"/> at all.
/// </para>
/// <para>
/// Every other read is genuinely read-only and applies <c>AsNoTracking</c>: the listings (<see
/// cref="ListAsync"/>, <see cref="GetAllAsync"/>), the tenant resolutions (<see cref="GetByAliasAsync"/>,
/// <see cref="GetByTabAsync"/>) and the projections (<see cref="GetRoleNamesAsync"/>, <see
/// cref="CountUsersForPortalsAsync"/>, <see cref="CountPagesForPortalsAsync"/>).
/// </para>
/// </remarks>
internal sealed class PortalRepository : IPortalRepository
{
    /// <summary>Ordering applied when the caller names no sortable property.</summary>
    /// <remarks>
    /// Chosen to match the legacy default rather than invented: the terminal paging procedure selected
    /// <c>ORDER BY PortalName</c> (<c>04.04.00.SqlDataProvider</c>, <c>GetPortalsByName</c>), so an
    /// unsorted request produces the sequence the legacy administration grid produced.
    /// </remarks>
    private const string DefaultSortProperty = "PortalName";

    /// <summary>
    /// Character that removes the special meaning of a <c>LIKE</c> metacharacter in the name pattern this
    /// repository builds.
    /// </summary>
    private const string LikeEscapeCharacter = "\\";

    /// <summary>The page tally reported for a portal that does not exist or records no administration page.</summary>
    /// <remarks>
    /// Minus one is what the terminal <c>GetTabCount</c> returned in both of those cases and is therefore
    /// preserved rather than smoothed to zero. The procedure read <c>@AdminTabId</c> into a variable and
    /// then compared every row against it; a null made each row's predicate UNKNOWN, so <c>COUNT(*)</c> was
    /// nought and <c>COUNT(*) - 1</c> was minus one.
    /// </remarks>
    private const int NoAdministrationPageTally = -1;

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="PortalRepository"/> class.</summary>
    /// <param name="dbContext">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The context is the only collaborator. No cache, clock, logger, request accessor, password hasher,
    /// file system or service locator is injected, so nothing this type does can depend on ambient state -
    /// which is precisely what the reflection-resolved <c>DataProvider.Instance()</c> singleton it replaces
    /// could not promise.
    /// </remarks>
    public PortalRepository(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    /// <inheritdoc />
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
            string wanted = nameFilter.Trim();
            string pattern = LikePrefixPattern(wanted);

            // ⚠ FAIL CLOSED WHEN THE FILTER CANNOT FILTER. A filter made only of supplementary characters
            // carries no weight in this schema's collation, so the comparison degrades to one that matches
            // every row - and the screen goes on announcing an active filter over the complete record set.
            // See CollationSafeFilter for the measurement and for the two remedies that were rejected.
            query = CollationSafeFilter.CannotDiscriminate(wanted)
                ? query.Where(_ => false)
                : query.Where(p => EF.Functions.Like(p.PortalName, pattern, LikeEscapeCharacter));
        }

        query = ApplyOrder(query, sortBy, descending);

        if (pageSize == 0)
        {
            // A page size of zero requests every match, which is how the callers that need a complete
            // tenant list ask for one without inventing a sentinel page size.
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
    // MIGRATION: replaces `GetPortals` and the ArrayList its controller counterpart returned.
    public async Task<IReadOnlyList<Portal>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Ordered for the same reason the paged read is ordered: a caller that walks the whole installation
        // must see a stable sequence between calls. The primary key terminates the order so tenants sharing
        // a name still have a defined relative position.
        return await _dbContext.Portals
            .AsNoTracking()
            .OrderBy(p => p.PortalName)
            .ThenBy(p => p.PortalId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    // Replaces `GetPortal`, whose reader the controller hydrated column by column.
    public async Task<Portal?> GetByIdAsync(int portalId, bool includeAliases = false, CancellationToken cancellationToken = default)
    {
        // PortalID is IDENTITY(-1, 1), so both -1 and 0 are legitimate keys and neither may be treated as
        // "absent" - the shipped default portal really is portal 0 and the first generated tenant really is
        // -1, while -1 is simultaneously the legacy Null.NullInteger marker.
        if (!includeAliases)
        {
            // RESOLVED ONCE PER REQUEST, THEN REUSED - and that is the whole point of asking by key here.
            // FindAsync consults this scoped context's change tracker BEFORE it consults the database, so
            // the second and every later ask for the same portal inside one request costs no round trip at
            // all. FirstOrDefaultAsync, which this used to be, always issues one: a performance review
            // measured the tenant portal being read twice on a single DELETE /users/{id} - the same
            // statement, the same @portalId, twenty-one milliseconds apart - once by the request pipeline
            // resolving the tenant and again by the service doing the work. Every authenticated request
            // resolves its tenant, so this is the hot path rather than a corner of it.
            //
            // The substitution is exact, and both of the things that could have made it inexact were
            // checked rather than assumed. This context sets no NoTracking default, so entities really are
            // tracked and there really is something to find; and no entity in this model carries a global
            // query filter, which FindAsync would silently bypass. Identity resolution already meant that
            // FirstOrDefaultAsync handed back the tracked instance whenever one existed, so a caller
            // holding unsaved edits observed those edits before this change and observes them still - all
            // that has gone is the redundant SELECT that preceded them.
            Portal? tracked = await _dbContext.Portals
                .FindAsync(new object?[] { portalId }, cancellationToken)
                .ConfigureAwait(false);

            // ⚠ AND THE SAVING IS WITHDRAWN INSIDE A TRANSACTION, WHICH IS NOT A QUALIFICATION OF THE ABOVE
            // BUT A CORRECTION TO IT. Answering from the tracker is right for a read; it is wrong for a read
            // a caller opened a transaction to make authoritative, because no statement is issued and so no
            // row is locked. See TransactionalRead for the amendment that was lost while this answered from
            // memory inside a serialisable scope.
            return await TransactionalRead
                .InTransactionAsync(_dbContext, tracked, cancellationToken)
                .ConfigureAwait(false);
        }

        // Loaded only on request. The aliases are needed by the detail projection and by the update path
        // that rewrites them, and by nothing else, so a listing does not pay for them. This path keeps its
        // query because FindAsync cannot express an Include, and a caller that asked for the aliases needs
        // them loaded whether or not the portal itself is already tracked.
        Portal? withAliases = await _dbContext.Portals
            .Include(p => p.PortalAliases)
            .FirstOrDefaultAsync(p => p.PortalId == portalId, cancellationToken)
            .ConfigureAwait(false);

        // This branch DOES issue its statement, so the row is locked - but identity resolution keeps the
        // values of an instance already in hand, so inside a transaction the values still have to be taken
        // from what the store just returned.
        return await TransactionalRead
            .InTransactionAsync(_dbContext, withAliases, cancellationToken)
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

        // The historical tenant-resolution procedure `GetPortalSettings` matched the alias with a
        // leading-and-trailing wildcard - `where PortalAlias like '%' + @PortalAlias + '%'`
        // (01.00.00.SqlDataProvider:L4569 onward) - and then took `min(PortalID)` of whatever matched.
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

        // This is the terminal `GetPortalByTab` shape (02.02.02.SqlDataProvider:L3942), reproduced
        // relationally rather than literally.
        return await _dbContext.Portals
            .AsNoTracking()
            .Where(p => p.PortalAliases.Any(a => a.HttpAlias != null && a.HttpAlias.ToLower() == wanted)
                && p.Tabs.Any(t => t.TabId == tabId))
            .OrderBy(p => p.PortalId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Portals.AnyAsync(p => p.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TabBelongsToPortalAsync(int portalId, int tabId, CancellationToken cancellationToken = default)
    {
        // Asked of the page table rather than through the portal's navigation, so the answer is one
        // existence probe. A page that does not exist and a page belonging to another tenant are both
        // false, which is exactly what the legacy reader-returns-no-row result meant.
        return _dbContext.Tabs.AnyAsync(t => t.TabId == tabId && t.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.Portals.CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountUsersAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Membership of a tenant is the UserPortals row, not a column on Users, so the count is taken
        // there. Unauthorised members are included because the legacy portals grid counted every registered
        // account against its tenant regardless of authorisation state.
        return _dbContext.UserPortals.CountAsync(m => m.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
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
        // single-portal member uses - every UserPortals row counts, authorised or not - so the batched and
        // unbatched tallies cannot disagree.
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

        // The administration page of each requested portal, in one read. A portal that is absent from this
        // map either does not exist or records no administration page; the legacy statement could not
        // distinguish those two either, and both answer minus one below.
        Dictionary<int, int> administrationPages = await _dbContext.Portals
            .AsNoTracking()
            .Where(portal => wanted.Contains(portal.PortalId) && portal.AdminTabId != null)
            .Select(portal => new PortalTally { PortalId = portal.PortalId, Count = portal.AdminTabId!.Value })
            .ToDictionaryAsync(row => row.PortalId, row => row.Count, cancellationToken)
            .ConfigureAwait(false);

        // Every page of every requested portal, WITH its parent, because the exclusion is expressed over
        // both the page's own key and its parent's.
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

    /// <summary>Counts one portal's pages the way the terminal <c>GetTabCount</c> counted them.</summary>
    /// <param name="portalId">The portal to tally.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>
    /// The legacy page tally, which is minus one for a portal that does not exist or records no
    /// administration page.
    /// </returns>
    /// <remarks>
    /// A faithful translation of <c>04.04.00.SqlDataProvider</c> lines 511-527, in its own order: read
    /// <c>AdminTabId</c> from the portal, then <c>SELECT COUNT(*) - 1</c> over the portal's pages excluding
    /// the administration page and its direct children.
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
            // Reached both when the portal does not exist and when it records no administration page. The
            // legacy statement could not distinguish them either: comparing against a null @AdminTabId made
            // every row's predicate UNKNOWN, so COUNT(*) was 0 and the expression returned 0 - 1.
            return NoAdministrationPageTally;
        }

        int counted = await _dbContext.Tabs
            .AsNoTracking()
            .CountAsync(
                tab => tab.PortalId == portalId
                    && tab.TabId != administrationTabId
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
    private static bool ExcludesAdministrationPage(int tabId, int? parentId, int administrationTabId) =>
        tabId != administrationTabId && (parentId is null || parentId.Value != administrationTabId);

    /// <summary>Expands a grouped tally into a total map over the identifiers that were asked for.</summary>
    /// <remarks>
    /// A grouped read returns no row for an identifier with nothing to count, so the projection is sparse.
    /// The batched contract promises a TOTAL map, which is what lets a caller index it directly instead of
    /// remembering that an absent key means zero, so the zeros are filled in here rather than at every call
    /// site.
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

    /// <summary>Carries one grouped tally row out of a batched count.</summary>
    /// <remarks>
    /// A named type rather than an anonymous one because the projection is consumed by a shared helper, and
    /// an anonymous type cannot be named in that helper's signature.
    /// </remarks>
    private sealed class PortalTally
    {
        /// <summary>Gets or sets the portal the tally belongs to.</summary>
        public int PortalId { get; set; }

        /// <summary>Gets or sets the number of rows counted for that portal.</summary>
        public int Count { get; set; }
    }

    /// <summary>Carries one page's own key and its parent out of a batched page read.</summary>
    /// <remarks>
    /// The batched page tally needs both keys, because the legacy exclusion is expressed over the page's
    /// own identifier AND its parent's. A named type rather than an anonymous one for the same reason <see
    /// cref="PortalTally"/> is named: the projection is consumed by a shared predicate whose signature has
    /// to name it.
    /// </remarks>
    private sealed class PageParentage
    {
        /// <summary>Gets or sets the portal the page belongs to.</summary>
        public int PortalId { get; set; }

        /// <summary>
        /// Gets or sets the page's own key. <c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>, so zero is
        /// genuine.
        /// </summary>
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

        // RoleID is IDENTITY(0, 1), so zero is a legitimate role key. Only roles that actually exist are
        // returned, which is what lets a caller distinguish an unset assignment from one that points at a
        // role somebody has since deleted.
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
    // Collapses two legacy insert paths onto one entity. `AddPortalInfo` took FOURTEEN positional arguments
    // and wrote two aggregates - it created the portal's administrator from the name, surname, username,
    // password and address handed to it while `CreatePortal` took NINE and wrote the Portals row alone.
    public Task AddAsync(Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Portals.Add(portal);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    // One member replaces two procedures that wrote the SAME Portals row from opposite ends -
    // `UpdatePortalInfo` with TWENTY-SEVEN positional arguments covering the descriptive and configuration
    // columns, and `UpdatePortalSetup` with NINE covering the administrator and the well-known page
    // assignments.
    public Task UpdateAsync(Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        cancellationToken.ThrowIfCancellationRequested();

        // The detached branch ASSIGNS THE STATE and must never call DbSet.Update, and that is a correctness
        // requirement of this schema rather than a stylistic preference.
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

    /// <summary>Applies a deterministic ordering to a tenant query.</summary>
    /// <param name="query">The query to order.</param>
    /// <param name="sortBy">The sortable property the caller named, or <see langword="null"/>.</param>
    /// <param name="descending">Whether the named property is applied in descending order.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// An ordering is applied unconditionally, including when the caller names nothing and when the caller
    /// names something this repository does not recognise. Skip-and-take paging over an unordered
    /// relational query has no defined row assignment, so a page could otherwise repeat or omit rows
    /// between requests.
    /// </remarks>
    // The arms below are exactly the five names the boundary admits for this collection, and that
    // correspondence is the point rather than a coincidence.
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
    /// <summary>Turns literal filter text into a <c>LIKE</c> pattern that matches it as a prefix.</summary>
    /// <param name="text">The literal text to match at the start of a tenant name.</param>
    /// <returns>
    /// A pattern for use with <see cref="LikeEscapeCharacter"/> as the escape character, matching any name
    /// that begins with <paramref name="text"/>.
    /// </returns>
    /// <remarks>
    /// Every metacharacter in the caller's text is escaped, so the match is a literal prefix rather than a
    /// pattern the caller can influence.
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
