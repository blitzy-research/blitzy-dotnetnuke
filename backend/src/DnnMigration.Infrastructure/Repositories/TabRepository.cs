using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

// The long positional writes collapse into entity staging, and the generated key moves.

// Page names are NOT unique in the terminal schema. IX_Tabs over (PortalID, TabName) was added by
// 01.00.08.SqlDataProvider line 6072 and dropped again by 02.00.01.SqlDataProvider line 64, so duplicates
// are legal storage rather than corrupt data.

// Orchestration and computed projections are not persistence, and none of them is implemented here.

/// <summary>
/// Reads the <c>dbo.Tabs</c> and <c>dbo.TabModules</c> rows that form a portal's page hierarchy and stages
/// writes against them, implementing <see cref="ITabRepository"/> over <see cref="DnnDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The type takes exactly one dependency - the context - and deliberately takes no cache, no clock, no
/// logger, no permission evaluator, no HTTP accessor and no sibling repository.
/// </para>
/// <para>
/// DELETION HAS TWO DISTINCT MEANINGS AND THEY ARE KEPT APART, because the legacy schema kept them apart.
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
    /// Ports <c>GetTab</c>, whose terminal definition is the whole of <c>WHERE TabId = @TabId</c>
    /// (04.04.00.SqlDataProvider lines 462-467).
    /// </remarks>
    public Task<Tab?> GetByIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return _context.Tabs
            .AsNoTracking()
            .FirstOrDefaultAsync(tab => tab.TabId == tabId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read-only and ordered by key, so a caller receives the same sequence for the same set whichever
    /// order the keys arrived in. Duplicated keys are collapsed rather than yielding a row twice.
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByIdsAsync(
        IReadOnlyCollection<int> tabIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabIds);

        if (tabIds.Count == 0)
        {
            return Array.Empty<Tab>();
        }

        int[] wanted = tabIds.Distinct().ToArray();

        return await _context.Tabs
            .AsNoTracking()
            .Where(tab => wanted.Contains(tab.TabId))
            .OrderBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetTabs</c>, whose terminal definition filtered on the portal alone and ordered by
    /// <c>TabOrder, TabName</c> (04.04.00.SqlDataProvider lines 440-448). The primary key is appended to
    /// that ordering and nothing is substituted into it, so the sequence stays the legacy sequence while
    /// becoming total.
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByPortalIdAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        // The portal is matched as a value, never as a range or a wildcard: -1 and 0 are both genuine
        // portal keys in this schema, so neither can be read as "every portal" or "no portal".
        return await _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == portalId)
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabName)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// No predicate is applied to <c>IsDeleted</c>: a recycled page is returned, exactly as the two sibling
    /// reads return one, and excluding it is the caller's policy.
    /// </remarks>
    public Task<Tab?> GetPortalTabAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return _context.Tabs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                tab => tab.PortalId == portalId && tab.TabId == tabId,
                cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetAllTabs</c>, whose terminal definition carried no portal predicate at all and ordered by
    /// <c>TabOrder, TabName</c> (04.04.00.SqlDataProvider lines 475-480).
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .AsNoTracking()
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabName)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetTabByName</c>, whose terminal definition filtered on the name AND the portal and ordered
    /// its matches by key - <c>where TabName = @TabName and ((PortalId = @PortalId) or (@PortalId is null
    /// AND PortalId is null)) order by TabID</c> (04.04.00.SqlDataProvider lines 490-503).
    /// </remarks>
    public Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        // The comparison is lowered on both sides rather than expressed as a string.Equals overload taking
        // a StringComparison.
        return _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == portalId && tab.TabName.ToLower() == wanted)
            .OrderBy(tab => tab.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The narrowing is now absolute, where the legacy helper's was not. <c>GetTabByNameAndParent</c>
    /// scanned the portal-wide matches for one whose parent agreed and, finding none, RETURNED THE FIRST
    /// MATCH ANYWAY - so asking for a page under one parent could yield a page under another. This member
    /// returns <see langword="null"/> instead.
    /// </remarks>
    public Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        int parentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        // Lowered on both sides for the reason given on the portal-scoped overload: a StringComparison
        // argument cannot be translated to SQL.
        return _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == portalId
                && tab.ParentId == parentId
                && tab.TabName.ToLower() == wanted)
            .OrderBy(tab => tab.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetTabsByParentId</c>, whose terminal definition filtered on the parent alone and ordered
    /// by the portal-wide sequence - <c>WHERE ParentId = @ParentId ORDER BY TabOrder</c>
    /// (04.04.00.SqlDataProvider lines 422-430).
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByParentIdAsync(
        int parentId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.ParentId == parentId)
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabId)
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
            .AsNoTracking()
            .Where(tab => tab.ParentId == parentId && tab.PortalId == portalId)
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>SET @AdminTabId = (SELECT AdminTabId FROM Portals WHERE PortalID = @PortalID)</c> then <c>SELECT
    /// COUNT(*) - 1 FROM Tabs WHERE (PortalID = @PortalID) AND (TabID &lt;&gt; @AdminTabId) AND (ParentId
    /// &lt;&gt; @AdminTabId OR ParentId IS NULL)</c>.
    /// </remarks>
    public async Task<int> CountByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        int? adminTabId = await _context.Portals
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
            return -1;
        }

        int counted = await _context.Tabs
            .AsNoTracking()
            .CountAsync(
                tab => tab.PortalId == portalId
                    && tab.TabId != administrationTabId
                    && (tab.ParentId == null || tab.ParentId != administrationTabId),
                cancellationToken)
            .ConfigureAwait(false);

        return counted - 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetTabModules</c> - "which modules are placed on this page" - whose terminal definition is
    /// <c>WHERE TabId = @TabId ORDER BY ModuleOrder</c> (04.04.00.SqlDataProvider lines 601-609).
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetTabModulesAsync(
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .AsNoTracking()
            .Where(placement => placement.TabId == tabId)
            .OrderBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The ordering matches <see cref="GetTabModulesAsync"/>, since the legacy pair ordered identically by
    /// <c>TM.ModuleOrder</c>.
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetPortalTabModulesAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .AsNoTracking()
            .Where(placement => placement.TabId == tabId && placement.Tab.PortalId == portalId)
            .OrderBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: and it is a DIVERGENCE, not a reproduction. The terminal legacy sub-select tested the
    /// parent relationship alone, so a page whose children had all been recycled still reported
    /// <c>HasChildren = true</c> and the tree rendered an expander that opened onto nothing.
    /// </remarks>
    public async Task<IReadOnlyCollection<int>> ListParentTabIdsAsync(
        int? portalId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Tab> children = _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.ParentId != null && !tab.IsDeleted);

        if (portalId is int owningPortalId)
        {
            // Composed as a separate clause on the resolved value rather than as a comparison against the
            // nullable argument, so that the "not restricted" case cannot degenerate into a search for
            // pages whose portal column is null - which is a different and real stored state.
            children = children.Where(tab => tab.PortalId == owningPortalId);
        }

        List<int> parents = await children
            .Select(tab => tab.ParentId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A set, because the contract promises the caller's per-row membership test is a hash lookup
        // rather than a linear scan of a list it would otherwise have to convert itself.
        return new HashSet<int>(parents);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reproduces the correlated sub-select that the legacy portal read view resolved into the
    /// <c>SuperTabId</c> column of its result set - <c>select TabId from Tabs where PortalId is null and
    /// ParentId is null</c> - and that every later revision of that view carried unchanged.
    /// </remarks>
    public Task<int?> GetHostRootTabIdAsync(CancellationToken cancellationToken = default)
    {
        return _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == null && tab.ParentId == null)
            .OrderBy(tab => tab.TabId)
            .Select(tab => (int?)tab.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The reporting form of the name lookup: it answers the uniqueness question without materialising a
    /// page, which is what a validator needs, and it reports a fact instead of raising because the terminal
    /// schema does not enforce uniqueness - <c>IX_Tabs</c> over <c>(PortalID, TabName)</c> was added by
    /// 01.00.08.SqlDataProvider line 6072 and dropped again by 02.00.01.SqlDataProvider line 64.
    /// </remarks>
    public Task<bool> TabNameExistsAsync(
        int? portalId,
        string tabName,
        int? excludingTabId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        IQueryable<Tab> candidates = _context.Tabs
            .AsNoTracking()
            .Where(tab => !tab.IsDeleted && tab.TabName.ToLower() == wanted);

        candidates = portalId is int owningPortalId
            ? candidates.Where(tab => tab.PortalId == owningPortalId)
            : candidates.Where(tab => tab.PortalId == null);

        if (excludingTabId is int disregarded)
        {
            // So that renaming a page does not collide with itself. Composed on the resolved value, so
            // that "exclude none" is the absence of a clause rather than a reserved number.
            candidates = candidates.Where(tab => tab.TabId != disregarded);
        }

        return candidates.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>AddTab</c>, whose eighteen positional arguments are replaced by the entity. The page is
    /// stored as supplied: assigning its position in the sequence, its depth, its path and its parent, and
    /// validating any of them, all happen before this member is called.
    /// </remarks>
    public Task AddAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        _context.Tabs.Add(tab);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A detached page is attached and marked modified in full, which reproduces the legacy procedure's own
    /// behaviour of writing every column it was handed. Since reads are detached this is the ordinary path;
    /// a page that some other operation has already tracked is left to the change tracker, whose pending
    /// modifications this call must not widen.
    /// </remarks>
    // The detached branch assigns EntityState.Modified DIRECTLY and must never call DbSet.Update, and that
    // is a correctness requirement of this schema rather than a stylistic preference.
    public Task UpdateAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<Tab> entry = _context.Entry(tab);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>UpdateTabOrder</c>, whose five positional arguments are all properties of the page and
    /// whose terminal procedure wrote exactly FOUR columns - <c>set TabOrder = @TabOrder, [Level] = @Level,
    /// ParentId = @ParentId, TabPath = @TabPath where TabId = @TabId</c> (04.05.00.SqlDataProvider lines
    /// 1815-1828).
    /// </remarks>
    // The detached branch assigns EntityState.Unchanged DIRECTLY and must never call DbSet.Attach, for the
    // same reason UpdateAsync must never call DbSet.Update: Attach also passes forceStateWhenUnknownKey:
    // EntityState.Added, so attaching a detached page whose TabID is 0 - the real first page of an
    // installation, because dbo.Tabs.TabID is IDENTITY(0, 1) - staged an INSERT and then marked four
    // properties modified on a row that was being added rather than updated.
    public Task UpdateOrderAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<Tab> entry = _context.Entry(tab);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Unchanged;

            entry.Property(candidate => candidate.TabOrder).IsModified = true;
            entry.Property(candidate => candidate.Level).IsModified = true;
            entry.Property(candidate => candidate.ParentId).IsModified = true;
            entry.Property(candidate => candidate.TabPath).IsModified = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page is addressed by key so that a caller need not read one in order to remove it, and 0 is a
    /// real key. Absence is not an error: passing an identifier no row bears stages nothing, so a caller
    /// that has already established absence need not distinguish the two cases.
    /// </remarks>
    public async Task DeleteAsync(int tabId, CancellationToken cancellationToken = default)
    {
        Tab? tab = await _context.Tabs
            .FirstOrDefaultAsync(candidate => candidate.TabId == tabId, cancellationToken)
            .ConfigureAwait(false);

        if (tab is null)
        {
            return;
        }

        _context.Tabs.Remove(tab);
    }
}
