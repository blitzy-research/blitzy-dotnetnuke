using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
    /// The ordering is the hierarchy ordering, and it is produced here rather than by the caller.
    /// <c>Tabs.TabOrder</c> is a single portal-wide sequence that the legacy ordering routine
    /// maintained in steps so that a parent always precedes its own children and siblings keep their
    /// relative positions; ordering by it therefore yields depth-first hierarchy order directly.
    /// <c>Level</c> and the primary key are appended only as tie-breakers, so that rows sharing a
    /// position still have a stable relative order and skip-and-take paging over the same data
    /// cannot repeat or omit a row.
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> ListAsync(int portalId, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Tab> query = _context.Tabs.Where(t => t.PortalId == portalId);

        if (!includeDeleted)
        {
            query = query.Where(t => !t.IsDeleted);
        }

        return await query
            .OrderBy(t => t.TabOrder)
            .ThenBy(t => t.Level)
            .ThenBy(t => t.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Tab?> GetAsync(int tabId, CancellationToken cancellationToken = default)
    {
        // TabID is IDENTITY(0, 1), so zero is a legitimate key. Absence is reported as null.
        return _context.Tabs.FirstOrDefaultAsync(t => t.TabId == tabId, cancellationToken);
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
    public Task<bool> TabNameExistsAsync(int? portalId, string tabName, int? excludingTabId = null, CancellationToken cancellationToken = default)
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
}
