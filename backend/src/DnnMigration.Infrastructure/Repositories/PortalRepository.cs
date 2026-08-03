using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
/// No read member applies <c>AsNoTracking</c>. That is a deliberate correctness decision rather than
/// an oversight: the application layer obtains an entity from a read member, mutates it, and commits
/// through <see cref="IUnitOfWork"/>, which only works while the entity is tracked by the same
/// context instance. Detaching listings to save change-tracker work would silently discard those
/// mutations for any caller that updated a row it had listed.
/// </para>
/// </remarks>
internal sealed class PortalRepository : IPortalRepository
{
    /// <summary>Ordering applied when the caller names no sortable property.</summary>
    private const string DefaultSortProperty = "PortalName";

    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="PortalRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PortalRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
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
        IQueryable<Portal> query = _context.Portals;

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            // The legacy portals grid matched a fragment of the site name anywhere in the value.
            // Lower-casing both sides keeps the comparison case-insensitive irrespective of the
            // collation the installation happens to use, rather than relying on the SQL Server
            // default being case-insensitive.
            string wanted = nameFilter.Trim().ToLowerInvariant();
            query = query.Where(p => p.PortalName.ToLower().Contains(wanted));
        }

        query = ApplyOrder(query, sortBy, descending);

        if (pageSize == 0)
        {
            // A page size of zero requests every match, which is how the callers that need a
            // complete tenant list ask for one without inventing a sentinel page size.
            List<Portal> all = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return PagedResult<Portal>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<Portal> rows = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<Portal>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Portal>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Ordered for the same reason the paged read is ordered: a caller that walks the whole
        // installation must see a stable sequence between calls. The primary key terminates the
        // order so tenants sharing a name still have a defined relative position.
        return await _context.Portals
            .OrderBy(p => p.PortalName)
            .ThenBy(p => p.PortalId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Portal?> GetByIdAsync(int portalId, bool includeAliases = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Portal> query = _context.Portals;

        if (includeAliases)
        {
            query = query.Include(p => p.PortalAliases);
        }

        // PortalID is IDENTITY(-1, 1), so both -1 and 0 are legitimate keys and neither may be
        // treated as "absent". Absence is reported as null and never as a numeric sentinel.
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

        // MIGRATION: the legacy tenant-resolution procedure matched the alias with a
        // leading-and-trailing wildcard and then took the lowest matching identifier, so one
        // tenant's alias being a substring of another's could resolve a request to the wrong
        // tenant. The comparison is an equality test here. Case is normalised on both sides so
        // the result does not depend on the collation of the installation.
        return await _context.Portals
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

        // Both halves of the legacy check are required: the alias must resolve to a tenant AND
        // the page must belong to that same tenant. Evaluating them as one predicate is what
        // stops a page identifier from one tenant being read under another tenant's alias.
        return await _context.Portals
            .Where(p => p.PortalAliases.Any(a => a.HttpAlias != null && a.HttpAlias.ToLower() == wanted)
                && p.Tabs.Any(t => t.TabId == tabId))
            .OrderBy(p => p.PortalId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return _context.Portals.AnyAsync(p => p.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TabBelongsToPortalAsync(int portalId, int tabId, CancellationToken cancellationToken = default)
    {
        // Asked of the page table rather than through the portal's navigation, so the answer is
        // one existence probe. A page that does not exist and a page belonging to another tenant
        // are both false, which is exactly what the legacy reader-returns-no-row result meant.
        return _context.Tabs.AnyAsync(t => t.TabId == tabId && t.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return _context.Portals.CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountUsersAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Membership of a tenant is the UserPortals row, not a column on Users, so the count is
        // taken there. Unauthorised members are included because the legacy portals grid counted
        // every registered account against its tenant regardless of authorisation state.
        return _context.UserPortals.CountAsync(m => m.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountPagesAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Deletion of a page is the soft delete that backs the legacy recycle bin, so a page in the
        // bin is excluded from the tenant's page count exactly as the legacy grid excluded it.
        return _context.Tabs.CountAsync(t => t.PortalId == portalId && !t.IsDeleted, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> GetRoleNamesAsync(int portalId, CancellationToken cancellationToken = default)
    {
        var assignments = await _context.Portals
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
        List<KeyValuePair<int, string>> rows = await _context.Roles
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
    public Task AddAsync(Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged, not written. The key is assigned when the unit of work commits, which is why
        // this member yields no identifier: returning one would force a flush here and split the
        // multi-table portal creation into independently durable statements.
        _context.Portals.Add(portal);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        cancellationToken.ThrowIfCancellationRequested();

        // A portal read through this repository is already tracked, so its modifications are
        // staged by the tracker and this call is the caller's explicit statement of intent. An
        // untracked instance - one rebuilt outside this context - is attached and marked
        // modified so the same call works for it too, which keeps the contract honest for a
        // caller that did not obtain the entity from a read member here.
        if (_context.Entry(portal).State is EntityState.Detached)
        {
            _context.Portals.Update(portal);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Resolved before removal because the contract identifies its target by identifier, as
        // the legacy procedure did. When the caller already holds the entity, this resolves from
        // the change tracker without a round trip. Dependent rows are left to the schema's own
        // referential rules rather than a deletion order encoded here.
        Portal? portal = await _context.Portals
            .FirstOrDefaultAsync(p => p.PortalId == portalId, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null)
        {
            // Nothing to stage. Absence is not an error: a caller that has already established
            // absence need not distinguish the two cases.
            return;
        }

        _context.Portals.Remove(portal);
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
            "DESCRIPTION" => descending
                ? query.OrderByDescending(p => p.Description).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.Description).ThenBy(p => p.PortalId),
            "CURRENCY" => descending
                ? query.OrderByDescending(p => p.Currency).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.Currency).ThenBy(p => p.PortalId),
            _ => descending
                ? query.OrderByDescending(p => p.PortalName).ThenByDescending(p => p.PortalId)
                : query.OrderBy(p => p.PortalName).ThenBy(p => p.PortalId),
        };
    }
}
