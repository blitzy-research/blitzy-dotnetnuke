using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the <see cref="PortalAlias"/> host names that resolve a request to a tenant.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the alias slice of the legacy core data provider
/// (<c>Library/Components/Providers/Data/DataProvider.vb</c> lines 354-362) and the data-access half of
/// <c>Library/Components/Portal/PortalAliasController.vb</c>, including the three reflection-hydrator call
/// sites that file used. Eight stored-procedure invocations become LINQ over the tracked set.
/// </para>
/// <para>
/// MIGRATION: host names are compared case-insensitively rather than by the installation's collation,
/// because every legacy read and write of this table lower-cased the value first - PortalAliasController.vb
/// lines 31, 52, 76 and 97, and PortalSettings.vb line 1118. Matching is always on the whole stored value;
/// no member matches a fragment.
/// </para>
/// <para>
/// MIGRATION: the null test that accompanies every host-name comparison is required rather than defensive.
/// <c>dbo.PortalAlias.HTTPAlias</c> is declared without a NOT NULL clause at
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider</c> line 3807, so the domain
/// property is nullable. It translates to IS NOT NULL, which is what SQL already does with a null on either
/// side of an equality, so no row that used to match stops matching - and a row holding no host name cannot
/// be reached by a blank one.
/// </para>
/// </remarks>
internal sealed class PortalAliasRepository : IPortalAliasRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="PortalAliasRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PortalAliasRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public Task<PortalAlias?> GetByIdAsync(int portalAliasId, CancellationToken cancellationToken = default)
    {
        return _context.PortalAliases
            .FirstOrDefaultAsync(alias => alias.PortalAliasId == portalAliasId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PortalAlias?> GetByAliasAsync(string httpAlias, int portalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = Normalise(httpAlias);

        // Portal-scoped by design: the legacy procedure filtered on the host name AND the portal
        // (02.02.02.SqlDataProvider line 3846), which makes this "does this tenant answer on this host
        // name" rather than a resolution of a host name to a tenant.
        return _context.PortalAliases
            .FirstOrDefaultAsync(
                alias => alias.PortalId == portalId
                    && alias.HttpAlias != null
                    && alias.HttpAlias.ToLower() == wanted,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetAllByHttpAliasAsync(string httpAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = Normalise(httpAlias);

        // EVERY match is returned rather than one chosen row. The legacy resolution procedure collapsed
        // multiple matches with min(PortalID) and so could serve one tenant's content under another
        // tenant's host name; returning the matches lets the caller refuse an ambiguous host instead.
        // The schema's installation-wide unique constraint on HTTPAlias bounds this at one row for a
        // well-formed installation, so the unbounded read costs nothing and a second row is the signal
        // that the data is defective.
        //
        // The owning portal and its roles are loaded because the request-scoped tenant context built from
        // this result needs the portal's administrator and registered role NAMES, which are not columns on
        // Portals. Loading them here keeps tenant resolution to a single round trip.
        return await _context.PortalAliases
            .AsNoTracking()
            .Where(alias => alias.HttpAlias != null && alias.HttpAlias.ToLower() == wanted)
            .Include(alias => alias.Portal)
                .ThenInclude(portal => portal.Roles)
            .OrderBy(alias => alias.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> AliasExistsAsync(string httpAlias, int? excludingPortalAliasId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = Normalise(httpAlias);

        // Not portal-scoped: the uniqueness constraint spans every tenant, so a host name already bound
        // to another portal is still taken.
        IQueryable<PortalAlias> query = _context.PortalAliases
            .Where(alias => alias.HttpAlias != null && alias.HttpAlias.ToLower() == wanted);

        if (excludingPortalAliasId.HasValue)
        {
            // Excluding the row being edited is what lets an update keep its own host name without
            // reporting a collision against itself. The presence of a value selects the exclusion, never
            // its magnitude: this identity seeds at 1, but no value is read as "absent".
            int excluded = excludingPortalAliasId.Value;
            query = query.Where(alias => alias.PortalAliasId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // Scoped to exactly the portal bearing the identifier. PortalID is IDENTITY(-1, 1), so -1 and 0
        // are real tenants and neither is treated as a request for every portal - that question is
        // GetAllAsync, which is a separate member precisely so no magic value is needed here.
        return await Ordered(_context.PortalAliases.Where(alias => alias.PortalId == portalId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // MIGRATION: the explicit form of what the legacy code expressed by passing -1 to the
        // portal-scoped read ("where (PortalID = @PortalID or @PortalID = -1)",
        // 02.02.02.SqlDataProvider line 3861, reached from PortalAliasController.vb lines 86-88). The
        // wildcard is a distinct member here, so no caller supplies a magic identifier to mean "all".
        return await Ordered(_context.PortalAliases)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Portal?> GetPortalByAliasIdAsync(int portalAliasId, CancellationToken cancellationToken = default)
    {
        // Ports GetPortalByPortalAliasID (DataProvider.vb line 358), whose procedure joined the portal to
        // the alias and projected the portal's columns (02.02.02.SqlDataProvider line 3831). Expressed as
        // a projection from the alias so the join stays server-side and only the portal is materialised.
        return _context.PortalAliases
            .Where(alias => alias.PortalAliasId == portalAliasId)
            .Select(alias => alias.Portal)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalAlias);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged, not written. The key is assigned when the unit of work commits, which is why this
        // member yields no identifier: the legacy procedure returned one only because it ended with
        // select SCOPE_IDENTITY() (02.02.02.SqlDataProvider line 4080), and returning one here would
        // force a flush that split the multi-table portal creation - of which this table is one of five -
        // into independently durable statements.
        _context.PortalAliases.Add(portalAlias);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalAlias);
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: this is legacy UpdatePortalAliasInfo (DataProvider.vb line 360), which despite its
        // name executed the procedure that updated one identified row (02.02.02.SqlDataProvider line
        // 4096). Its three positional arguments are carried by the entity, so they cannot be transposed.
        //
        // An alias read through this repository is already tracked, so its modifications are staged by
        // the tracker and this call is the caller's explicit statement of intent. An untracked instance is
        // attached and marked modified so the same call works for it too.
        if (_context.Entry(portalAlias).State is EntityState.Detached)
        {
            _context.PortalAliases.Update(portalAlias);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int portalAliasId, CancellationToken cancellationToken = default)
    {
        // Resolved before removal because the contract identifies its target by identifier, as the legacy
        // procedure did (DataProvider.vb line 362). When the caller already holds the entity this
        // resolves from the change tracker without a round trip.
        PortalAlias? alias = await _context.PortalAliases
            .FirstOrDefaultAsync(candidate => candidate.PortalAliasId == portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        if (alias is null)
        {
            // Nothing to stage. Absence is not an error: a caller that has already established absence
            // need not distinguish the two cases.
            return;
        }

        _context.PortalAliases.Remove(alias);
    }

    /// <summary>
    /// Lower-cases and trims a supplied host name for comparison.
    /// </summary>
    /// <remarks>
    /// Comparison only. The stored value is never rewritten by a read, and normalising a value before it
    /// is stored belongs to the Application layer.
    /// </remarks>
    /// <param name="httpAlias">The host name supplied by the caller.</param>
    /// <returns>The value to compare against the stored column.</returns>
    private static string Normalise(string httpAlias) => httpAlias.Trim().ToLowerInvariant();

    /// <summary>
    /// Applies a deterministic ordering to an alias query.
    /// </summary>
    /// <remarks>
    /// The host name orders the result because that is what a reader recognises, and the key breaks ties
    /// so that a stable order is produced even when two rows carry the same or no host name.
    /// </remarks>
    /// <param name="query">The query to order.</param>
    /// <returns>The ordered query.</returns>
    private static IQueryable<PortalAlias> Ordered(IQueryable<PortalAlias> query)
    {
        return query
            .OrderBy(alias => alias.HttpAlias)
            .ThenBy(alias => alias.PortalAliasId);
    }
}
