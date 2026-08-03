using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the <see cref="Tab"/> page hierarchy through <see cref="DnnDbContext"/>.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the thirty-five page stored procedures reached from the legacy core data
/// provider and the data-access half of <c>Library/Components/Tabs/TabController.vb</c>. Deletion
/// remains the soft delete through <see cref="Tab.IsDeleted"/> that backs the legacy recycle bin, so
/// this contract exposes no hard-delete member at all.
/// <para>
/// No read member applies <c>AsNoTracking</c>, for the reason given on
/// <see cref="PortalRepository"/>: the application layer mutates entities it obtained from a read
/// member and commits them through <see cref="IUnitOfWork"/>.
/// </para>
/// </remarks>
internal sealed class TabRepository : ITabRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="TabRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public TabRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The ordering is the hierarchy ordering, and it is produced here rather than by the caller.
    /// <c>Tabs.TabOrder</c> is a single portal-wide sequence that the legacy ordering routine
    /// maintained in steps so that a parent always precedes its own children and siblings keep their
    /// relative positions; ordering by it therefore yields depth-first hierarchy order directly.
    /// <c>Level</c> and the primary key are appended only as tie-breakers, so that rows sharing a
    /// position still have a stable relative order and skip-and-take paging over the same data
    /// cannot repeat or omit a row. The legacy procedure's own tie-breaker was <c>TabName</c>
    /// (03.01.01.SqlDataProvider line 490); <c>Level</c> is preferred here because it keeps a parent
    /// ahead of its children when both share a sequence number, which the alphabetical tie-breaker
    /// did not guarantee and which the caller's tree computation relies on.
    /// </para>
    /// <para>
    /// Recycled pages are included, because the legacy procedure applied no predicate to
    /// <c>IsDeleted</c> and projected the column instead (03.01.01.SqlDataProvider lines 477 and
    /// 487-490). Hiding them here would change measured behaviour and would take a policy decision
    /// that belongs to the caller.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .Where(t => t.PortalId == portalId)
            .OrderBy(t => t.TabOrder)
            .ThenBy(t => t.Level)
            .ThenBy(t => t.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unbounded by design: this is the port of <c>GetAllTabs</c>, whose terminal definition carried no
    /// portal predicate at all (03.01.01.SqlDataProvider line 597). Host-level pages, whose portal
    /// column is null, are therefore included alongside every portal's pages.
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .OrderBy(t => t.PortalId)
            .ThenBy(t => t.TabOrder)
            .ThenBy(t => t.Level)
            .ThenBy(t => t.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Tab?> GetByIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        // TabID is IDENTITY(0, 1), so zero is a legitimate key. Absence is reported as null.
        return _context.Tabs.FirstOrDefaultAsync(t => t.TabId == tabId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The legacy procedure ordered its matches by key and the controller took the first
    /// (03.01.01.SqlDataProvider lines 534-536), so the lowest-keyed match is returned where a portal
    /// holds more than one page of the same name. Comparison ignores case, matching the collation the
    /// legacy equality predicate ran under.
    /// </remarks>
    public Task<Tab?> GetByNameAsync(string tabName, int portalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        return _context.Tabs
            .Where(t => t.PortalId == portalId && t.TabName.ToLower() == wanted)
            .OrderBy(t => t.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The parent-scoped variant. A supplied parent is matched as a value, so a page at the root of the
    /// tree - which stores no parent at all - is never returned by this overload.
    /// </remarks>
    public Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        int parentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        return _context.Tabs
            .Where(t => t.PortalId == portalId
                && t.ParentId == parentId
                && t.TabName.ToLower() == wanted)
            .OrderBy(t => t.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unscoped by portal and inclusive of recycled children, because the legacy procedure filtered on
    /// the parent alone and ordered by the portal-wide sequence (03.01.01.SqlDataProvider lines
    /// 448-449).
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByParentIdAsync(int parentId, CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .Where(t => t.ParentId == parentId)
            .OrderBy(t => t.TabOrder)
            .ThenBy(t => t.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Tab>> GetByParentIdAsync(
        int parentId,
        int portalId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .Where(t => t.ParentId == parentId && t.PortalId == portalId)
            .OrderBy(t => t.TabOrder)
            .ThenBy(t => t.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is deliberately NOT a plain row count, because the legacy procedure was not one. It resolved
    /// the portal's administration page and then counted the portal's pages excluding it, subtracting one
    /// further: <c>set @AdminTabId = (select AdminTabId from Portals where PortalID = @PortalID)</c> then
    /// <c>select count(*) - 1 from Tabs where (PortalID = @PortalID) and (TabID &lt;&gt; @AdminTabId)</c>
    /// (04.04.00.SqlDataProvider lines 511-525). Both adjustments are reproduced exactly, because the
    /// figure is the portal's advertised page total and is what the page quota is compared against.
    /// </para>
    /// <para>
    /// The administration page is resolved in a separate read rather than as a correlated sub-query so
    /// that the null case is explicit: a portal with no administration page recorded excludes nothing,
    /// which is the behaviour the legacy expression produced when the sub-select yielded null and the
    /// inequality therefore matched no row.
    /// </para>
    /// </remarks>
    public async Task<int> CountByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        int? adminTabId = await _context.Portals
            .Where(p => p.PortalId == portalId)
            .Select(p => p.AdminTabId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        IQueryable<Tab> query = _context.Tabs.Where(t => t.PortalId == portalId);

        if (adminTabId.HasValue)
        {
            int excluded = adminTabId.Value;
            query = query.Where(t => t.TabId != excluded);
        }

        int counted = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        // The legacy expression subtracted one unconditionally. Reproduced, negative results included:
        // this value is a metric to be reported and compared, never an identifier or a presence test.
        return counted - 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The placements of one page, ordered as the page renders them - by pane, then by the order within
    /// the pane, with the key as a deterministic tie-breaker.
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetTabModulesAsync(
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .Where(placement => placement.TabId == tabId)
            .OrderBy(placement => placement.PaneName)
            .ThenBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The portal argument is a tenant-isolation guard rather than a redundant filter, so the page's own
    /// ownership is tested and nothing is returned when the page belongs to another portal. The test is
    /// expressed against the page rather than against the placement because a placement carries no
    /// portal of its own.
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetPortalTabModulesAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .Where(placement => placement.TabId == tabId
                && placement.Tab.PortalId == portalId)
            .OrderBy(placement => placement.PaneName)
            .ThenBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One round trip answers the "has children" question for an entire listing. The legacy
    /// navigation query asked it once per row, which is the cost this member exists to remove. Pages
    /// in the recycle bin are ignored, because a parent whose only children are deleted has no
    /// children a caller can navigate to.
    /// </remarks>
    public async Task<IReadOnlyCollection<int>> ListParentTabIdsAsync(int? portalId, CancellationToken cancellationToken = default)
    {
        IQueryable<Tab> query = _context.Tabs.Where(t => t.ParentId != null && !t.IsDeleted);

        if (portalId.HasValue)
        {
            int wanted = portalId.Value;
            query = query.Where(t => t.PortalId == wanted);
        }

        List<int> parents = await query
            .Select(t => t.ParentId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A set is returned so that the caller's per-row membership test is a hash lookup rather
        // than a linear scan of a list it would otherwise have to convert itself.
        return new HashSet<int>(parents);
    }

    /// <inheritdoc />
    public Task<int?> GetHostRootTabIdAsync(CancellationToken cancellationToken = default)
    {
        // Host-level pages carry no portal, and the host root additionally carries no parent. The
        // cast to a nullable integer is what distinguishes "no such page" from the page whose
        // identifier is zero, which TabID being IDENTITY(0, 1) makes a real possibility.
        return _context.Tabs
            .Where(t => t.PortalId == null && t.ParentId == null)
            .OrderBy(t => t.TabId)
            .Select(t => (int?)t.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The comparison is portal-wide rather than sibling-wide, matching the scope of the unique index
    /// the legacy schema briefly carried - <c>IX_Tabs</c> over <c>(PortalID, TabName)</c>, added by
    /// the 01.00.08 script and dropped again by 02.00.01. Because that index no longer exists in the
    /// terminal schema, uniqueness is a rule the application layer applies rather than one the store
    /// enforces, which is precisely why this member reports rather than throws. Pages in the recycle
    /// bin are excluded so that deleting a page genuinely frees its name for reuse.
    /// </remarks>
    public Task<bool> TabNameExistsAsync(
        int? portalId,
        string tabName,
        int? excludingTabId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        IQueryable<Tab> query = _context.Tabs
            .Where(t => !t.IsDeleted && t.TabName.ToLower() == wanted);

        if (portalId.HasValue)
        {
            int owner = portalId.Value;
            query = query.Where(t => t.PortalId == owner);
        }
        else
        {
            // A null portal identifier addresses the host-level pages, which are the rows whose
            // PortalID column is itself null rather than rows belonging to some portal numbered
            // zero or minus one - both of which are legitimate portal keys here.
            query = query.Where(t => t.PortalId == null);
        }

        if (excludingTabId.HasValue)
        {
            int excluded = excludingTabId.Value;
            query = query.Where(t => t.TabId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged, not written. The key is assigned when the unit of work commits, which is why this
        // member yields no identifier: the legacy procedure returned one only because it ended with
        // select SCOPE_IDENTITY(), and returning one here would force a flush that split the
        // multi-table portal creation - of which this table is one of five - into independently
        // durable statements.
        _context.Tabs.Add(tab);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: this is both legacy UpdateTab overloads at once (DataProvider.vb lines 111 and
        // 112), whose only difference was a RefreshInterval/PageHeadText/IsSecure tail. The entity
        // carries every one of those arguments, so they cannot be transposed, and the overload pair
        // does not survive into the target.
        //
        // A page read through this repository is already tracked, so its modifications are staged by
        // the tracker and this call is the caller's explicit statement of intent. An untracked instance
        // is attached and marked modified so the same call works for it too.
        if (_context.Entry(tab).State is EntityState.Detached)
        {
            _context.Tabs.Update(tab);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The legacy procedure wrote exactly four columns - <c>TabOrder</c>, <c>[Level]</c>,
    /// <c>ParentId</c> and <c>TabPath</c> (04.05.00.SqlDataProvider lines 1815-1828) - and that
    /// narrowness is preserved by marking only those four properties modified when the page is not
    /// already tracked. A renumbering pass therefore cannot carry a page's unrelated edits along with
    /// it.
    /// </remarks>
    public Task UpdateOrderAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<Tab> entry = _context.Entry(tab);

        if (entry.State is EntityState.Detached)
        {
            _context.Tabs.Attach(tab);

            entry.Property(t => t.TabOrder).IsModified = true;
            entry.Property(t => t.Level).IsModified = true;
            entry.Property(t => t.ParentId).IsModified = true;
            entry.Property(t => t.TabPath).IsModified = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A hard delete, matching the legacy procedure's literal
    /// <c>delete from Tabs where TabId = @TabId</c> (02.00.00.SqlDataProvider lines 1446-1455). Sending
    /// a page to the recycle bin is a change to <see cref="Tab.IsDeleted"/> staged through
    /// <see cref="UpdateAsync"/>, and this member is not a substitute for it.
    /// </remarks>
    public async Task DeleteAsync(int tabId, CancellationToken cancellationToken = default)
    {
        // Resolved before removal because the contract identifies its target by identifier, as the
        // legacy procedure did. When the caller already holds the entity this resolves from the change
        // tracker without a round trip.
        Tab? tab = await _context.Tabs
            .FirstOrDefaultAsync(candidate => candidate.TabId == tabId, cancellationToken)
            .ConfigureAwait(false);

        if (tab is null)
        {
            // Nothing to stage. Absence is not an error: a caller that has already established absence
            // need not distinguish the two cases.
            return;
        }

        _context.Tabs.Remove(tab);
    }
}
